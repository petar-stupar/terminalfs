using System.Diagnostics;

namespace TerminalFs.Core;

/// <summary>
/// One command: the text that was written, the process it became, and the files a caller reads
/// to find out what happened.
/// </summary>
/// <remarks>
/// <para>
/// An id is one command and only one. It is taken when the header is written, spent when the
/// process is started, and never reusable: a second <c>run</c> naming it is refused until the
/// directory is removed, and what appears afterwards is a new command with empty output rather
/// than more of the old one. The alternative — appending a second command's output to the
/// first's — leaves a caller unable to say which bytes belong to which, and unable to read an
/// exit code that means anything.
/// </para>
/// <para>
/// The open-handle count lives here rather than on a handler because a handler is per-fid: the
/// server builds a fresh one for every walk and clunks each one separately, so no handler knows
/// whether it was the last. Removal has to wait for the last, which only this object can see.
/// </para>
/// </remarks>
public sealed class Command
{
    private readonly Lock gate = new();
    private readonly TimeProvider time;
    private readonly TimeSpan keep;
    private readonly Action<Command> onExpire;
    private readonly TaskCompletionSource exited =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly HashSet<string> hidden = new(StringComparer.Ordinal);

    private CommandState state = CommandState.Reserved;
    private Process? process;
    private int? pid;
    private int? exitCode;
    private int leases;
    private ITimer? timer;
    private bool pinned;
    private bool retired;
    private uint statusRevision;
    private DateTimeOffset changedAt;

    internal Command(
        string id,
        string directory,
        TimeSpan keep,
        TimeProvider time,
        Action<Command> onExpire)
    {
        Id = id;
        Directory = directory;
        this.keep = keep;
        this.time = time;
        this.onExpire = onExpire;

        CreatedAt = time.GetUtcNow();
        changedAt = CreatedAt;

        System.IO.Directory.CreateDirectory(directory);

        Stdout = new OutputFile(Path.Combine(directory, "stdout"), CreatedAt);
        Stderr = new OutputFile(Path.Combine(directory, "stderr"), CreatedAt);
    }

    /// <summary>The name the caller gave it.</summary>
    public string Id { get; }

    /// <summary>Where its output files live, outside the served tree.</summary>
    public string Directory { get; }

    /// <summary>When the id was taken.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>What was written after the header. Empty until the control file closes.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Everything the process wrote to its standard output.</summary>
    public OutputFile Stdout { get; }

    /// <summary>Everything it wrote to its standard error, and anything this program had to add.</summary>
    public OutputFile Stderr { get; }

    /// <summary>Where it has got to.</summary>
    public CommandState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>The process's id while it runs, and nothing once it has stopped.</summary>
    public int? Pid
    {
        get
        {
            lock (gate)
            {
                return pid;
            }
        }
    }

    /// <summary>What it exited with, once it has.</summary>
    public int? ExitCode
    {
        get
        {
            lock (gate)
            {
                return exitCode;
            }
        }
    }

    /// <summary>Whether it has stopped, however it stopped.</summary>
    public bool HasExited => State is CommandState.Completed or CommandState.Error;

    /// <summary>Whether the registry has let go of it.</summary>
    public bool Retired
    {
        get
        {
            lock (gate)
            {
                return retired;
            }
        }
    }

    /// <summary>How many handles are open on its files.</summary>
    public int Leases
    {
        get
        {
            lock (gate)
            {
                return leases;
            }
        }
    }

    /// <summary>Moves on every change of state, so a qid version can say the file changed.</summary>
    public uint StatusRevision
    {
        get
        {
            lock (gate)
            {
                return statusRevision;
            }
        }
    }

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

    /// <summary>What <c>status</c> holds.</summary>
    public string StatusLine => State switch
    {
        CommandState.Completed => "completed\n",
        CommandState.Error => "error\n",
        _ => "running\n",
    };

    /// <summary>
    /// The files its directory lists: the ones that carry something, less the ones a caller has
    /// already unlinked.
    /// </summary>
    /// <remarks>
    /// <c>pid</c> is there only while there is a process to have one, and <c>exitcode</c> only
    /// once there is a code. A caller can therefore tell a running command from a finished one by
    /// looking at the directory, without reading anything.
    /// </remarks>
    public IReadOnlyList<string> VisibleChildren
    {
        get
        {
            lock (gate)
            {
                return VisibleChildrenLocked();
            }
        }
    }

    /// <summary>Takes a handle. While one is held the command is not removed by the timer.</summary>
    public Lease Open()
    {
        lock (gate)
        {
            leases++;
            Disarm();
        }

        return new Lease(this);
    }

    /// <summary>
    /// Takes a child out of the listing, as an unlink does. Returns whether it was there.
    /// </summary>
    /// <remarks>
    /// Unlinking pins the command against the timer for good. A caller removing children one by
    /// one is dismantling the directory, and having the timer retire it halfway through would
    /// turn the rest of their <c>rm -r</c> into a string of errors about files that were there a
    /// moment ago.
    /// </remarks>
    public bool Hide(string child)
    {
        lock (gate)
        {
            if (retired)
            {
                // Already gone. Saying so as success is what keeps an rm -r racing the timer
                // from failing halfway.
                return true;
            }

            if (!VisibleChildrenLocked().Contains(child))
            {
                return false;
            }

            hidden.Add(child);
            pinned = true;
            Disarm();

            return true;
        }
    }

    /// <summary>Waits for it to stop, or for <paramref name="timeout"/>, whichever is first.</summary>
    /// <returns>Where it had got to when the wait ended.</returns>
    public async Task<CommandState> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (HasExited)
        {
            return State;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task finished = exited.Task;
        Task elapsed = Task.Delay(timeout, time, cancellation.Token);

        Task first = await Task.WhenAny(finished, elapsed).ConfigureAwait(false);

        if (first == finished)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        else
        {
            // A cancelled wait is a flushed read: the caller has gone, and the reason to be here
            // with it.
            cancellationToken.ThrowIfCancellationRequested();
        }

        return State;
    }

    /// <summary>Ends it, and everything it started.</summary>
    /// <remarks>
    /// The whole process tree, because a shell that has forked is not stopped by killing the
    /// shell: the children reparent and go on writing to the pipes this program is reading, and
    /// the command never finishes.
    /// </remarks>
    public void Kill()
    {
        Process? running;

        lock (gate)
        {
            running = process;
        }

        if (running is null)
        {
            return;
        }

        try
        {
            running.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                or NotSupportedException or AggregateException)
        {
            // It stopped between the check and the kill, or the platform would not walk the tree.
            Diagnostics.Report($"killing {Id}", exception);
        }
    }

    internal void MarkStarted(Process started)
    {
        lock (gate)
        {
            process = started;
            pid = started.Id;
            state = CommandState.Running;
            Touch();
        }
    }

    internal void MarkText(string text)
    {
        lock (gate)
        {
            Text = text;
        }
    }

    internal void MarkExited(int code)
    {
        lock (gate)
        {
            if (state is CommandState.Completed or CommandState.Error)
            {
                return;
            }

            exitCode = code;
            pid = null;
            process = null;
            state = code == 0 ? CommandState.Completed : CommandState.Error;
            Touch();
        }

        Stdout.CloseWriter();
        Stderr.CloseWriter();
        exited.TrySetResult();

        ArmIfIdle();
    }

    /// <summary>
    /// Records a command that never became a process: a shell that would not start, a body that
    /// was never written, a rule that refused it.
    /// </summary>
    internal void MarkFailed(string reason)
    {
        DateTimeOffset now = time.GetUtcNow();

        lock (gate)
        {
            if (state is CommandState.Completed or CommandState.Error)
            {
                return;
            }

            exitCode = -1;
            pid = null;
            process = null;
            state = CommandState.Error;
            Touch();
        }

        // The reason goes where a caller already looks for one. An exit code of -1 with an empty
        // stderr says only that something went wrong.
        Stderr.Append("terminalfs: " + reason + "\n", now);

        Stdout.CloseWriter();
        Stderr.CloseWriter();
        exited.TrySetResult();

        ArmIfIdle();
    }

    /// <summary>Takes it out of the registry, ending it first if it is still going.</summary>
    /// <remarks>
    /// A running command is killed and then left alone: the supervisor is still following the
    /// process and will record the exit when it arrives. Completing the wait here instead would
    /// answer a caller with the state as it was a moment before the kill — <c>running</c>, for a
    /// command that is being destroyed — which is worse than making them wait the extra
    /// millisecond for the truth.
    /// </remarks>
    internal void Retire()
    {
        bool running;
        bool started;

        lock (gate)
        {
            if (retired)
            {
                return;
            }

            retired = true;
            running = state is CommandState.Running;
            started = state is not CommandState.Reserved;
            Disarm();
        }

        if (running)
        {
            Kill();
        }
        else if (!started)
        {
            // Nothing ever ran, so nothing will arrive to complete the wait.
            MarkFailed("removed before it ran");
        }

        ReleaseIfIdle();
    }

    /// <summary>Starts the removal timer if there is nothing left to wait for.</summary>
    internal void ArmIfIdle()
    {
        lock (gate)
        {
            if (timer is not null || retired || pinned || leases > 0)
            {
                return;
            }

            if (state is not (CommandState.Completed or CommandState.Error))
            {
                return;
            }

            timer = time.CreateTimer(_ => onExpire(this), null, keep, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Deletes what is on disk, once the registry has let go and nobody is reading.
    /// </summary>
    /// <remarks>
    /// Windows will not remove a directory while a handle inside it is open, and a reader that
    /// has just been told the command is gone may still be closing. The retries cover that; after
    /// them the registry's own sweep at shutdown is the backstop.
    /// </remarks>
    internal void ReleaseIfIdle()
    {
        lock (gate)
        {
            if (!retired || leases > 0)
            {
                return;
            }
        }

        Stdout.Release();
        Stderr.Release();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    Diagnostics.Report($"removing {Directory}", exception);
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// The listing, in the order a reader meets it: what was asked for, what is answering, then
    /// what it said.
    /// </summary>
    private List<string> VisibleChildrenLocked()
    {
        var names = new List<string>(8) { "command" };

        if (pid is not null)
        {
            names.Add("pid");
        }

        names.Add("status");

        if (exitCode is not null)
        {
            names.Add("exitcode");
        }

        names.Add("stdout");
        names.Add("stderr");
        names.Add("wait");

        // Only while there is something to end. A kill file on a command that has stopped is a
        // file whose only effect would be nothing.
        if (state is CommandState.Running)
        {
            names.Add("kill");
        }

        return hidden.Count == 0 ? names : [.. names.Where(name => !hidden.Contains(name))];
    }

    private void Touch()
    {
        statusRevision++;
        changedAt = time.GetUtcNow();
    }

    private void Disarm()
    {
        timer?.Dispose();
        timer = null;
    }

    private void Close()
    {
        lock (gate)
        {
            leases--;
        }

        ArmIfIdle();
        ReleaseIfIdle();
    }

    /// <summary>One open handle on one of a command's files.</summary>
    public sealed class Lease : IDisposable
    {
        private Command? command;

        internal Lease(Command command) => this.command = command;

        /// <inheritdoc />
        public void Dispose()
        {
            Command? held = Interlocked.Exchange(ref command, null);

            held?.Close();
        }
    }
}
