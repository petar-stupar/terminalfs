namespace TerminalFs.Core;

/// <summary>
/// A name taken under <c>/ctl</c> that is not yet a command: a file a caller can write to,
/// list, remove and rename, and which has produced nothing under <c>/cmd</c>.
/// </summary>
/// <remarks>
/// <para>
/// A draft gets its verdict at the moment nothing can change it: the bytes at the close, because
/// more bytes may still come; a refusal at the write, because nothing can un-refuse it. Until
/// then it is a held name and nothing else — no process, no output directory, no
/// <c>/cmd/&lt;name&gt;</c>. A name nobody ever wrote a command for therefore leaves nothing
/// behind, which a name that made a directory the moment it was opened could not.
/// </para>
/// <para>
/// A draft that has a verdict does not become a command at once. It settles, and a rename
/// arriving in that window moves it, so the command runs under the name the caller meant. That
/// window is what makes a client which writes to a temporary name and renames it into place work
/// here: such a client closes the temporary file <em>before</em> it renames, so the close is the
/// only signal we get, and spawning there would run the command under a name nobody chose.
/// Rename is not a route to running something — it is rename, applied to a name that has not
/// been decided yet.
/// </para>
/// <para>
/// One clock, two meanings: an undecided draft nobody is writing is discarded after
/// <c>--keep</c>, which frees a name a caller took and walked away from; a decided one commits
/// after <c>--settle</c>. It is never both at once, so it is one timer.
/// </para>
/// </remarks>
public sealed class Draft
{
    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly TimeSpan keep;
    private readonly TimeSpan settle;
    private readonly Action<Draft> onKeepElapsed;
    private readonly Action<Draft> onSettleElapsed;

    private string name;
    private bool awaitingFirstOpen = true;
    private bool sessionOpen;
    private string? text;
    private string? refusal;
    private bool claimed;
    private bool discarded;
    private ITimer? timer;
    private uint revision;
    private DateTimeOffset changedAt;

    internal Draft(
        string name,
        long ordinal,
        TimeSpan keep,
        TimeSpan settle,
        TimeProvider time,
        Action<Draft> onKeepElapsed,
        Action<Draft> onSettleElapsed)
    {
        this.name = name;
        this.keep = keep;
        this.settle = settle;
        this.time = time;
        this.onKeepElapsed = onKeepElapsed;
        this.onSettleElapsed = onSettleElapsed;

        Ordinal = ordinal;
        CreatedAt = time.GetUtcNow();
        changedAt = CreatedAt;

        // From the creation, not from the first open: a create whose open never arrives — a
        // client that died between the two, or a core that refused in between — would otherwise
        // hold the name for the life of the server.
        ArmKeep();
    }

    /// <summary>The name it currently answers to, which a rename changes.</summary>
    public string Name
    {
        get
        {
            lock (gate)
            {
                return name;
            }
        }
    }

    /// <summary>
    /// What identifies this draft whatever it is called.
    /// </summary>
    /// <remarks>
    /// A rename moves the name and not the file, so the qid must not move with it — see
    /// <see cref="Key"/>.
    /// </remarks>
    public long Ordinal { get; }

    /// <summary>
    /// The key the 9P layer derives a qid path from.
    /// </summary>
    /// <remarks>
    /// Deliberately not the path. A qid identifies a file for as long as that file exists, and
    /// the server matches on it when it rebases a fid's ancestry after a rename — so a qid that
    /// moved with the name would match nothing and leave every live fid pointing at a stale
    /// parent. <c>#</c> is outside the alphabet <see cref="CommandId"/> allows, so this cannot
    /// collide with a name.
    /// </remarks>
    public string Key => "/ctl#" + Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>When the name was taken.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset ChangedAt
    {
        get
        {
            lock (gate)
            {
                return changedAt;
            }
        }
    }

    /// <summary>Moves on every change, so a qid version can say the file changed.</summary>
    public uint Revision
    {
        get
        {
            lock (gate)
            {
                return revision;
            }
        }
    }

    /// <summary>
    /// Whether a verdict has been reached and this is only waiting to become a command.
    /// </summary>
    public bool Decided
    {
        get
        {
            lock (gate)
            {
                return text is not null || refusal is not null;
            }
        }
    }

    /// <summary>
    /// How long the command written to it is, once one has been; zero until then.
    /// </summary>
    /// <remarks>
    /// Zero while the file can still be written, because a client that believes a file has
    /// contents treats a write as a modification of them: macOS smbfs once laid a command over
    /// the front of what it thought was already there and sent the whole thing, and what ran was
    /// the command followed by the tail of this file's own help. Nothing to merge into means
    /// nothing merged.
    /// </remarks>
    /// <value>
    /// Once it has been decided the hazard is gone — a decided name cannot be opened again, by
    /// anyone — and the length is worth telling the truth about, because a client that writes a
    /// file and then checks what it wrote gets an answer rather than a silent zero.
    /// </value>
    public int Length
    {
        get
        {
            lock (gate)
            {
                return text is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(text);
            }
        }
    }

    /// <summary>Whether the registry has let go of it, however it let go.</summary>
    public bool Gone
    {
        get
        {
            lock (gate)
            {
                return claimed || discarded;
            }
        }
    }

    /// <summary>
    /// Takes the open the create is entitled to. True once, for the caller that made it.
    /// </summary>
    /// <remarks>
    /// The create and the open that follows it are two messages, and between them another caller
    /// could walk to the name and open it — winning a name they did not make. The node this
    /// returns is handed only to the fid that created it, so holding the first open is a property
    /// of that object rather than of a fid, and the model stays free of the protocol.
    /// </remarks>
    internal bool TryClaimFirstOpen()
    {
        lock (gate)
        {
            if (!awaitingFirstOpen || claimed || discarded)
            {
                return false;
            }

            awaitingFirstOpen = false;
            sessionOpen = true;
            Disarm();
            Touch();

            return true;
        }
    }

    /// <summary>Opens it for writing. False when it cannot be opened, whatever the reason.</summary>
    internal bool TryOpen()
    {
        lock (gate)
        {
            if (awaitingFirstOpen || sessionOpen || claimed || discarded)
            {
                return false;
            }

            if (text is not null || refusal is not null)
            {
                return false;
            }

            sessionOpen = true;
            Disarm();
            Touch();

            return true;
        }
    }

    /// <summary>Why an open was refused, for a caller who can read a sentence.</summary>
    internal string Unavailable()
    {
        lock (gate)
        {
            if (sessionOpen || awaitingFirstOpen)
            {
                return $"'{name}' is being written now; wait for that control file to close";
            }

            if (text is not null || refusal is not null)
            {
                return $"'{name}' has a command waiting to run; a name runs once";
            }

            return $"'{name}' is gone";
        }
    }

    /// <summary>Notes that the control file closed with nothing decided.</summary>
    internal void EndSession()
    {
        lock (gate)
        {
            sessionOpen = false;
            Touch();

            // A decided draft is on the settle clock already, and this must not push it back onto
            // the keep clock: the caller is finished and the command is waiting to run.
            if (text is null && refusal is null)
            {
                ArmKeep();
            }
        }
    }

    /// <summary>
    /// Records the bytes to run. Returns whether there is no settle delay to wait out.
    /// </summary>
    internal bool Ready(string command)
    {
        lock (gate)
        {
            if (claimed || discarded || text is not null || refusal is not null)
            {
                return false;
            }

            text = command;
            sessionOpen = false;
            Touch();

            return ArmSettle();
        }
    }

    /// <summary>
    /// Records a command a rule would not let start. Returns whether there is no delay to wait
    /// out.
    /// </summary>
    /// <remarks>
    /// Its clock starts here rather than at the close, because a refusal is final the moment it
    /// is given: the caller already has the error and nothing they write afterwards can change
    /// it. A refused draft settles like any other, so a caller who writes to a temporary name and
    /// renames it finds the reason under the name they meant.
    /// </remarks>
    internal bool Refuse(string command, string why)
    {
        lock (gate)
        {
            if (claimed || discarded || text is not null || refusal is not null)
            {
                return false;
            }

            text = command;
            refusal = why;
            Touch();

            return ArmSettle();
        }
    }

    /// <summary>
    /// Takes the verdict, once. The caller that gets true is the one that makes the command.
    /// </summary>
    /// <remarks>
    /// This one-shot is the whole of "a name runs at most once": a settle timer and a lookup can
    /// arrive together, and only one of them can leave with the bytes.
    /// </remarks>
    internal bool TryClaim(out string command, out string? why)
    {
        lock (gate)
        {
            command = text ?? string.Empty;
            why = refusal;

            if (claimed || discarded || text is null)
            {
                return false;
            }

            claimed = true;
            Disarm();

            return true;
        }
    }

    /// <summary>Gives it another name, without touching either clock.</summary>
    /// <remarks>
    /// The clock is not restarted. A caller who renames a decided draft is finished with it, and
    /// pushing the deadline back would only delay a command that has already been decided.
    /// </remarks>
    internal void Rename(string newName)
    {
        lock (gate)
        {
            name = newName;
            Touch();
        }
    }

    /// <summary>Frees the name without running anything. False if it was already gone.</summary>
    internal bool Discard()
    {
        lock (gate)
        {
            if (claimed || discarded)
            {
                return false;
            }

            discarded = true;
            Disarm();

            return true;
        }
    }

    private void ArmKeep()
    {
        Disarm();

        if (claimed || discarded)
        {
            return;
        }

        timer = time.CreateTimer(_ => onKeepElapsed(this), null, keep, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Starts the settle clock. Returns true when there is none and the caller must commit.</summary>
    private bool ArmSettle()
    {
        Disarm();

        if (claimed || discarded)
        {
            return false;
        }

        // No timer at all for a settle of zero, and the caller commits instead. A timer with a
        // due time of zero can run its callback before CreateTimer returns, and this is called
        // holding the draft's lock — a commit reached from in here would start a process inside
        // somebody else's critical section.
        if (settle <= TimeSpan.Zero)
        {
            return true;
        }

        timer = time.CreateTimer(_ => onSettleElapsed(this), null, settle, Timeout.InfiniteTimeSpan);

        return false;
    }

    private void Touch()
    {
        revision++;
        changedAt = time.GetUtcNow();
    }

    private void Disarm()
    {
        timer?.Dispose();
        timer = null;
    }
}
