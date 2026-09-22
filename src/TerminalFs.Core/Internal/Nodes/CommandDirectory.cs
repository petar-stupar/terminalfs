namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// One command's directory: <c>/cmd/&lt;id&gt;</c>.
/// </summary>
/// <remarks>
/// The children are built from the command's state each time they are asked for, because which
/// files exist is itself an answer: <c>pid</c> is there while there is a process to have one and
/// <c>exitcode</c> once there is a code, so a caller can tell a running command from a finished
/// one by listing the directory.
/// </remarks>
internal sealed class CommandDirectory : TerminalDirectory
{
    private readonly CommandRegistry registry;
    private readonly Command command;
    private readonly TimeSpan waitTimeout;

    internal CommandDirectory(CommandRegistry registry, Command command, TimeSpan waitTimeout)
        : base(
            command.Id,
            TerminalNodeKind.Command,
            "/cmd#" + command.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        this.registry = registry;
        this.command = command;
        this.waitTimeout = waitTimeout;
    }

    /// <inheritdoc />
    public override bool Writable => true;

    /// <inheritdoc />
    public override uint Revision => command.StatusRevision;

    /// <inheritdoc />
    public override DateTimeOffset? ModifiedAt => command.ChangedAt;

    /// <inheritdoc />
    public override Command.Lease? Lease() => command.Open();

    /// <inheritdoc />
    public override IReadOnlyList<TerminalNode> Children =>
        [.. command.VisibleChildren.Select(Build)];

    /// <inheritdoc />
    public override TerminalNode? Find(string name) =>
        command.VisibleChildren.Contains(name) ? Build(name) : null;

    /// <inheritdoc />
    public override void Remove(string name, bool directory)
    {
        if (directory)
        {
            throw new CommandException($"'{name}' is not a directory", CommandErrno.NotDirectory);
        }

        // Every child unlinks, kill included. Refusing that one looked like a helpful correction
        // — removing it ends nothing, writing to it does — but rm -r unlinks what it finds, and
        // a refusal there left the directory not empty, the removal abandoned halfway, and the
        // command it was supposed to stop still running.

        if (!command.Hide(name))
        {
            throw new CommandException($"there is no '{name}' here", CommandErrno.NotFound);
        }

        registry.Changed();
    }

    private TerminalNode Build(string name) => name switch
    {
        "stdout" => new TerminalOutput("stdout", Key + "/stdout", command, command.Stdout),
        "stderr" => new TerminalOutput("stderr", Key + "/stderr", command, command.Stderr),
        "wait" => new TerminalWait(Key + "/wait", command, waitTimeout),
        "kill" => new TerminalKill(Key + "/kill", command),
        _ => Field(name),
    };

    /// <summary>
    /// A one-value file. Every one of these ends in a newline, because a caller reads them with
    /// <c>cat</c> and a file without one runs into the next thing the terminal prints.
    /// </summary>
    private LivePage Field(string name)
    {
        Func<string> render = name switch
        {
            "command" => () => command.Text.EndsWith('\n') ? command.Text : command.Text + "\n",
            "status" => () => command.StatusLine,
            "pid" => () => command.Pid is { } pid
                ? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"
                : string.Empty,
            "reason" => () => command.Reason is { } why ? why + "\n" : string.Empty,
            "exitcode" => () => command.ExitCode is { } code
                ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"
                : string.Empty,
            _ => () => string.Empty,
        };

        return new LivePage(
            name,
            TerminalNodeKind.Field,
            Key + "/" + name,
            render,
            () => command.StatusRevision,
            () => command.ChangedAt,
            command.Open);
    }
}
