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

    /// <summary>
    /// How many recent refusals the control file reports.
    /// </summary>
    /// <remarks>
    /// Enough for a caller to find their own among a few others, few enough that the control
    /// file stays something you can read at a glance.
    /// </remarks>
    private const int RememberedRefusals = 8;

    private readonly ConcurrentDictionary<string, Command> commands = new(StringComparer.Ordinal);
    private readonly Queue<string> recentlyRetired = new();
    private readonly Queue<(DateTimeOffset When, string Reason)> refusals = new();
    private readonly Lock gate = new();
    private readonly CommandOptions options;

    private uint revision;
    private DateTimeOffset changedAt;
    private bool disposed;

    private CommandRegistry(CommandOptions options, string outputRoot)
    {
        this.options = options;
        OutputRoot = outputRoot;
        Shell = ShellSpec.Resolve(options.Shell);
        WorkingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);

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
                new LivePage(
                    "refused",
                    TerminalNodeKind.Field,
                    "/refused",
                    () => TreeText.Refused(this),
                    () => Revision,
                    () => ChangedAt),
                new CommandsDirectory(this, options.WaitTimeout),
                Skills.Directory(builtAt),
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

            if (commands.ContainsKey(name))
            {
                throw new CommandException(
                    $"'{name}' is already a command; a name runs once, so remove it or use another",
                    CommandErrno.Exists);
            }

            var command = new Command(
                name,
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
            command.MarkFailed("no command text was written before the control file was closed");
            Touch();
            return;
        }

        if (options.Deny.Match(text) is { } rule)
        {
            command.MarkFailed(Refusal(rule));
            Touch();
            return;
        }

        ProcessSupervisor.Spawn(command, text, Shell, WorkingDirectory, options.TimeProvider);
        Touch();
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
    /// Opens <c>/ctl/&lt;id&gt;</c>, taking the name for a command whose text has not arrived yet.
    /// </summary>
    /// <exception cref="CommandException">The id is not usable, or is already taken.</exception>
    public ControlSession OpenControl(string id) => new(this, Reserve(id), options.MaxControlBytes);

    /// <summary>
    /// Records a refusal so that a caller can find out what it was.
    /// </summary>
    /// <remarks>
    /// This exists because of what the protocol can carry. A refusal travels as a sentence and a
    /// number, but 9P2000.L — which is what a Linux mount and the SMB bridge both speak — carries
    /// only the number: every sentence here reaches a mounted caller as "Invalid argument" or
    /// "Operation not permitted" and nothing else. Since the reason cannot come back through the
    /// write, it is put where a caller can go and look for it, which is the file they were
    /// writing to.
    /// </remarks>
    public void Refused(string reason)
    {
        lock (gate)
        {
            refusals.Enqueue((options.TimeProvider.GetUtcNow(), reason));

            while (refusals.Count > RememberedRefusals)
            {
                refusals.Dequeue();
            }

            revision++;
        }
    }

    /// <summary>The refusals a caller may still be looking for the reason behind, newest last.</summary>
    public IReadOnlyList<(DateTimeOffset When, string Reason)> Refusals
    {
        get
        {
            lock (gate)
            {
                return [.. refusals];
            }
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
