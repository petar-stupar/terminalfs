using System.Collections.Concurrent;
using TerminalFs.Core.Internal;
using TerminalFs.Core.Internal.Nodes;
using TerminalFs.Core.Permissions;

namespace TerminalFs.Core;

/// <summary>
/// Every command this server has been asked to run, and the tree that describes them.
/// </summary>
/// <remarks>
/// This is the whole of the model. It knows about ids, processes, output and the rules that
/// refuse a command; it knows nothing about fids, qids or the wire, which is what lets the
/// protocol above it be tested without a socket and this be tested without a client.
/// </remarks>
public sealed class CommandRegistry : IDisposable
{
    /// <summary>
    /// How many just-removed ids are remembered. A caller's <c>rm -r</c> is several requests, and
    /// the timer can retire a command between any two of them; without this the rest of their
    /// removal fails on files that were there a moment ago. Sixty-four is far more than a client
    /// has in flight and costs nothing to keep.
    /// </summary>
    private const int RememberedRemovals = 64;

    private readonly ConcurrentDictionary<string, Command> commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
    private readonly Queue<string> recentlyRetired = new();
    private readonly Lock gate = new();
    private readonly CommandOptions options;

    private long nextOrdinal;
    private uint revision;
    private DateTimeOffset changedAt;
    private bool disposed;

    private CommandRegistry(CommandOptions options, string outputRoot)
    {
        this.options = options;
        OutputRoot = outputRoot;
        Shell = ShellSpec.Resolve(options.Shell);
        WorkingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        MountPath = options.MountPath;

        DateTimeOffset builtAt = options.TimeProvider.GetUtcNow();
        changedAt = builtAt;

        Root = new StaticDirectory(
            string.Empty,
            TerminalNodeKind.Root,
            "/",
            [
                new LivePage(
                    "index.md",
                    TerminalNodeKind.Root,
                    "/index.md",
                    () => TreeText.RootIndex(this, builtAt),
                    () => Revision,
                    () => ChangedAt),
                new ControlDirectory(this, builtAt),
                new CommandsDirectory(this, options.WaitTimeout),
                Skills.Directory(builtAt, options.MountPath),
            ]);
    }

    /// <summary>The root of the served tree.</summary>
    public TerminalDirectory Root { get; }

    /// <summary>Where the output files live.</summary>
    public string OutputRoot { get; }

    /// <summary>The shell every command is handed to.</summary>
    public ShellSpec Shell { get; }

    /// <summary>The directory every command runs in.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Where this tree can be read from, or null when nobody has said.</summary>
    public string? MountPath { get; }

    /// <summary>What the registry was told to do.</summary>
    public CommandOptions Options => options;

    /// <summary>Moves whenever the set of commands or any of their states changes.</summary>
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

    /// <summary>When a command was last added or removed.</summary>
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

    /// <summary>The names taken under <c>/ctl</c> that are not commands yet, oldest first.</summary>
    public IReadOnlyList<Draft> Drafts
    {
        get
        {
            lock (gate)
            {
                return [.. drafts.Values.OrderBy(draft => draft.CreatedAt).ThenBy(draft => draft.Ordinal)];
            }
        }
    }

    /// <summary>The commands, oldest first.</summary>
    public IReadOnlyList<Command> Commands =>
        [.. commands.Values.OrderBy(command => command.CreatedAt).ThenBy(command => command.Id, StringComparer.Ordinal)];

    /// <summary>Opens a registry, and the directory its commands will write into.</summary>
    public static CommandRegistry Create(CommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string root = options.OutputRoot
            ?? Path.Combine(Path.GetTempPath(), "terminalfs", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Directory.CreateDirectory(root);
        SweepAbandoned(root);

        return new CommandRegistry(options, root);
    }

    /// <summary>The command by that name, or null.</summary>
    public Command? Find(string id) =>
        commands.TryGetValue(id, out Command? command) ? command : null;

    /// <summary>
    /// Takes an id for a command whose text has not been written yet.
    /// </summary>
    /// <exception cref="CommandException">The id is not usable, or is already taken.</exception>
    public Command Reserve(string id)
    {
        string name = CommandId.Require(id);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            RequireFree(name);

            var command = new Command(
                name,
                nextOrdinal++,
                Path.Combine(OutputRoot, name),
                options.KeepAfterExit,
                options.TimeProvider,
                Expire);

            commands[name] = command;
            Touch();

            return command;
        }
    }

    /// <summary>
    /// Starts <paramref name="command"/> on the text that was written for it.
    /// </summary>
    /// <remarks>
    /// The rules are consulted once more here even though the write that carried the text was
    /// checked as it arrived. A command can reach a denied shape only at the end — the first
    /// write says <c>cd /tmp</c> and the second adds <c>&amp;&amp; sudo ls</c> — and this is the
    /// last moment at which refusing it still means it never ran.
    /// </remarks>
    public void Start(Command command, string text)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(text);

        command.MarkText(text);

        if (command.Retired)
        {
            return;
        }

        if (text.Trim().Length == 0)
        {
            // A control file closed with nothing in it never reaches here — it stays a draft.
            // This is a caller who wrote whitespace and meant it, or one driving the model.
            Deny(command, text, "there is no command in what was written");
            return;
        }

        if (options.Deny.Match(text) is { } rule)
        {
            Deny(command, text, Refusal(rule));
            return;
        }

        ProcessSupervisor.Spawn(command, text, Shell, WorkingDirectory, options.TimeProvider);
        Touch();
    }

    /// <summary>
    /// Records the bytes a draft was closed on, and commits it if there is no settle window.
    /// </summary>
    internal void Ready(Draft draft, string text)
    {
        if (draft.Ready(text))
        {
            Commit(draft);
        }
    }

    /// <summary>
    /// Records a command a rule would not let start, and commits it if there is no settle window.
    /// </summary>
    internal void Refuse(Draft draft, string text, string reason)
    {
        if (draft.Refuse(text, reason))
        {
            Commit(draft);
        }

        Touch();
    }

    /// <summary>
    /// Records a command refused before it ran. It keeps its name and its directory.
    /// </summary>
    /// <remarks>
    /// The reason has to be somewhere a caller can go and read it. A refusal travels as a sentence
    /// and a number, but 9P2000.L — which is what a Linux mount and the SMB bridge both speak —
    /// carries only the number, so every sentence reaches a mounted caller as "Operation not
    /// permitted" and nothing else. Putting it on the command names it: the three files in that
    /// directory are the whole of the answer, and nothing else could be true of one that never ran.
    /// </remarks>
    internal void Deny(Command command, string text, string reason)
    {
        if (command.MarkDenied(text, reason))
        {
            Touch();
        }
    }

    /// <summary>Ends a running command.</summary>
    /// <exception cref="CommandException">There is no command by that name.</exception>
    public void Kill(string id)
    {
        Command command = Require(id);

        command.Kill();
        Touch();
    }

    /// <summary>The sentence a refused command is given.</summary>
    public string Refusal(DenyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return options.Settings is { } settings
            ? $"denied by rule '{rule.Text}' in {settings.Path}"
            : $"denied by rule '{rule.Text}'";
    }

    /// <summary>
    /// Takes a command out of the tree, ending it first if it is still running.
    /// </summary>
    /// <exception cref="CommandException">
    /// There is no command by that name, or it still has children a caller can see.
    /// </exception>
    public void Remove(string id)
    {
        Command? command;

        lock (gate)
        {
            if (!commands.TryGetValue(id, out command))
            {
                // Removing what the timer has just taken is not an error: the caller asked for it
                // to be gone and it is gone.
                if (recentlyRetired.Contains(id))
                {
                    return;
                }

                throw new CommandException($"there is no command '{id}'", CommandErrno.NotFound);
            }

            if (command.VisibleChildren.Count > 0)
            {
                throw new CommandException(
                    $"'{id}' still has files in it",
                    CommandErrno.NotEmpty);
            }

            Forget(id);
        }

        command.Retire();
        Touch();
    }

    /// <summary>
    /// Takes a command out of the tree whatever is still in it, as removing its directory does.
    /// </summary>
    /// <remarks>
    /// This is what a <c>rm -r</c> reaches after it has unlinked the children, and what a caller
    /// who removes the directory outright means. A running command is killed: the directory is
    /// the only handle on it, and leaving a process alive whose output has nowhere to go would
    /// be leaving it to run for ever unobserved.
    /// </remarks>
    public void RemoveTree(string id)
    {
        Command? command;

        lock (gate)
        {
            if (!commands.TryGetValue(id, out command))
            {
                if (recentlyRetired.Contains(id))
                {
                    return;
                }

                throw new CommandException($"there is no command '{id}'", CommandErrno.NotFound);
            }

            Forget(id);
        }

        command.Retire();
        Touch();
    }

    /// <summary>Notes that something about a command changed, so the tree can say so.</summary>
    public void Changed() => Touch();

    /// <summary>
    /// Takes a name under <c>/ctl</c> for a command that has not been written yet.
    /// </summary>
    /// <exception cref="CommandException">The id is not usable, or is already taken.</exception>
    public Draft CreateDraft(string id)
    {
        string name = CommandId.Require(id);

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RequireFree(name);

            var draft = new Draft(
                name,
                nextOrdinal++,
                options.KeepAfterExit,
                options.Settle,
                options.TimeProvider,
                Discard,
                Commit);

            drafts[name] = draft;
            Touch();

            return draft;
        }
    }

    /// <summary>The draft by that name, or null.</summary>
    public Draft? FindDraft(string id)
    {
        lock (gate)
        {
            return drafts.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Opens a draft's control file.
    /// </summary>
    /// <param name="draft">The draft to write to.</param>
    /// <param name="claiming">
    /// Whether this is the open the create is entitled to, which nobody else can take.
    /// </param>
    /// <exception cref="CommandException">Something else is writing it, or it has been decided.</exception>
    public ControlSession OpenControl(Draft draft, bool claiming)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (!(claiming ? draft.TryClaimFirstOpen() : draft.TryOpen()))
        {
            throw new CommandException(draft.Unavailable(), CommandErrno.Exists);
        }

        return new ControlSession(this, draft, options.MaxControlBytes);
    }

    /// <summary>Gives a draft another name.</summary>
    /// <exception cref="CommandException">
    /// There is no draft by that name, the new name is not usable, or it is already taken.
    /// </exception>
    public void RenameDraft(string oldName, string newName)
    {
        string to = CommandId.Require(newName);

        lock (gate)
        {
            if (!drafts.TryGetValue(oldName, out Draft? draft))
            {
                throw new CommandException(
                    commands.ContainsKey(oldName)
                        ? $"'{oldName}' has already run; a command's name is fixed once it has run"
                        : $"there is no '{oldName}' here",
                    commands.ContainsKey(oldName) ? CommandErrno.NotPermitted : CommandErrno.NotFound);
            }

            if (string.Equals(oldName, to, StringComparison.Ordinal))
            {
                return;
            }

            // Never over the top of something. POSIX rename replaces what is there; here what is
            // there is either a draft somebody else is writing or a command's whole output, and
            // destroying either of those silently is the one thing this tree does not do.
            RequireFree(to);

            drafts.Remove(oldName);
            draft.Rename(to);
            drafts[to] = draft;
            Touch();
        }
    }

    /// <summary>Frees a name that has not run. </summary>
    /// <exception cref="CommandException">There is no draft by that name.</exception>
    public void RemoveDraft(string name)
    {
        lock (gate)
        {
            if (!drafts.TryGetValue(name, out Draft? draft) || !draft.Discard())
            {
                throw new CommandException($"there is no '{name}' here", CommandErrno.NotFound);
            }

            drafts.Remove(name);
            Touch();
        }
    }

    /// <summary>
    /// Makes <paramref name="name"/> a command now, if it is a draft that has been decided.
    /// </summary>
    /// <remarks>
    /// Called by anything that would reveal whether <c>/cmd/&lt;name&gt;</c> exists. Never call
    /// it, or its sibling below, while holding <see cref="gate"/>: committing starts a process.
    /// </remarks>
    public void Settle(string name)
    {
        Draft? draft;

        lock (gate)
        {
            draft = drafts.GetValueOrDefault(name);
        }

        if (draft is { Decided: true })
        {
            Commit(draft);
        }
    }

    /// <summary>Makes a command of every draft that has been decided.</summary>
    public void Settle()
    {
        Draft[] decided;

        lock (gate)
        {
            decided = [.. drafts.Values.Where(draft => draft.Decided)];
        }

        foreach (Draft draft in decided)
        {
            Commit(draft);
        }
    }

    /// <summary>Ends every command and takes the output directory with it.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        foreach (Command command in commands.Values)
        {
            command.Retire();
        }

        commands.Clear();

        // A draft that had been decided is a command that never runs, even though the caller's
        // write and its close both succeeded. That is uncomfortable and it is still right: the
        // alternative is starting a process during shutdown and killing it in the next breath,
        // which is what the loop above has just done to everything that was running.
        lock (gate)
        {
            foreach (Draft draft in drafts.Values)
            {
                draft.Discard();
            }

            drafts.Clear();
        }

        try
        {
            if (Directory.Exists(OutputRoot))
            {
                Directory.Delete(OutputRoot, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Report($"removing {OutputRoot}", exception);
        }
    }

    /// <summary>
    /// Turns a decided draft into a command and sets it going, once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decide under the lock, do the damage outside it, as <see cref="Expire"/> does. The whole
    /// visible transition happens under one lock — the name stops being a draft and starts being
    /// a command in one step — so no lookup can land in a gap where it is neither. That gap would
    /// be the very <c>ENOENT</c> this design exists to prevent.
    /// </para>
    /// <para>
    /// Starting the process happens outside the lock. <see cref="gate"/> is taken by every lookup
    /// and every listing in the tree, and holding it across a <c>Process.Start</c> would stop the
    /// whole server for as long as a shell takes to start — on a server whose whole job is
    /// starting shells.
    /// </para>
    /// </remarks>
    private void Commit(Draft draft)
    {
        Command command;
        string text;
        string? refusal;

        lock (gate)
        {
            if (disposed || !draft.TryClaim(out text, out refusal))
            {
                return;
            }

            // Read once: a rename could otherwise move it between the dictionary and the command.
            string name = draft.Name;

            // Making the command creates its directory and opens its two output files, which is
            // real work to do under a lock. It stays here anyway: two commits of one name racing
            // outside it would both truncate the same stdout.
            command = new Command(
                name,
                draft.Ordinal,
                Path.Combine(OutputRoot, name),
                options.KeepAfterExit,
                options.TimeProvider,
                Expire);

            drafts.Remove(name);
            commands[name] = command;
            Touch();
        }

        if (refusal is not null)
        {
            Deny(command, text, refusal);
        }
        else
        {
            Start(command, text);
        }
    }

    /// <summary>The keep timer's callback: free a name nobody wrote a command for.</summary>
    /// <remarks>
    /// A draft has no process to kill, no output to release and no directory to remove, so
    /// letting one go is nothing like retiring a command. Same clock, because a name is a name;
    /// a different callback, because there is nothing to tear down.
    /// </remarks>
    private void Discard(Draft draft)
    {
        lock (gate)
        {
            string name = draft.Name;

            if (!drafts.TryGetValue(name, out Draft? held)
                || !ReferenceEquals(held, draft)
                || !draft.Discard())
            {
                return;
            }

            drafts.Remove(name);
        }

        Touch();
    }

    /// <summary>
    /// Refuses a name that is already something. Callers hold <see cref="gate"/>.
    /// </summary>
    private void RequireFree(string name)
    {
        if (drafts.TryGetValue(name, out Draft? draft))
        {
            throw new CommandException(draft.Unavailable(), CommandErrno.Exists);
        }

        if (commands.ContainsKey(name))
        {
            throw new CommandException(
                $"'{name}' is already a command; a name runs once, so remove it or use another",
                CommandErrno.Exists);
        }
    }

    /// <summary>The timer's callback: retire a command nothing is waiting for any more.</summary>
    private void Expire(Command command)
    {
        lock (gate)
        {
            // Checked again under the lock, because a handle can have been opened between the
            // timer firing and this running.
            if (command.Retired || command.Leases > 0 || !command.HasExited)
            {
                return;
            }

            if (!commands.TryGetValue(command.Id, out Command? held) || !ReferenceEquals(held, command))
            {
                return;
            }

            Forget(command.Id);
        }

        command.Retire();
        Touch();
    }

    private Command Require(string id) =>
        Find(id) ?? throw new CommandException($"there is no command '{id}'", CommandErrno.NotFound);

    /// <summary>Drops an id and remembers that it was here. Callers hold <see cref="gate"/>.</summary>
    private void Forget(string id)
    {
        commands.TryRemove(id, out _);
        recentlyRetired.Enqueue(id);

        while (recentlyRetired.Count > RememberedRemovals)
        {
            recentlyRetired.Dequeue();
        }
    }

    private void Touch()
    {
        lock (gate)
        {
            revision++;
            changedAt = options.TimeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Removes the output of a server that is no longer running.
    /// </summary>
    /// <remarks>
    /// A directory here is named for the process that made it, so one whose process is gone was
    /// left by a server that was killed rather than stopped — its own cleanup never ran. Doing
    /// this at startup rather than at shutdown is what makes it happen at all in that case.
    /// </remarks>
    private static void SweepAbandoned(string root)
    {
        string? parent = Path.GetDirectoryName(root);

        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            string name = Path.GetFileName(directory);

            if (string.Equals(directory, root, StringComparison.Ordinal))
            {
                continue;
            }

            if (!int.TryParse(name, System.Globalization.CultureInfo.InvariantCulture, out int pid))
            {
                continue;
            }

            try
            {
                using System.Diagnostics.Process existing = System.Diagnostics.Process.GetProcessById(pid);

                continue;
            }
            catch (ArgumentException)
            {
                // No such process: the directory is nobody's.
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Report($"sweeping {directory}", exception);
            }
        }
    }
}
