namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/cmd</c>: one directory per command, and an index naming them all.
/// </summary>
internal sealed class CommandsDirectory : TerminalDirectory
{
    private readonly CommandRegistry registry;
    private readonly TimeSpan waitTimeout;
    private readonly TerminalPage index;

    internal CommandsDirectory(CommandRegistry registry, TimeSpan waitTimeout)
        : base("cmd", TerminalNodeKind.Commands, "/cmd")
    {
        this.registry = registry;
        this.waitTimeout = waitTimeout;

        index = new LivePage(
            "index.md",
            TerminalNodeKind.Commands,
            "/cmd/index.md",
            () => TreeText.CommandsIndex(registry),
            () => registry.Revision,
            () => registry.ChangedAt);
    }

    /// <inheritdoc />
    public override bool Writable => true;

    /// <inheritdoc />
    public override uint Revision => registry.Revision;

    /// <inheritdoc />
    public override DateTimeOffset? ModifiedAt => registry.ChangedAt;

    /// <inheritdoc />
    public override IReadOnlyList<TerminalNode> Children =>
        [index, .. registry.Commands.Select(Directory)];

    /// <inheritdoc />
    public override TerminalNode? Find(string name)
    {
        if (string.Equals(name, "index.md", StringComparison.Ordinal))
        {
            return index;
        }

        return registry.Find(name) is { } command ? Directory(command) : null;
    }

    /// <inheritdoc />
    public override void Remove(string name, bool directory)
    {
        if (string.Equals(name, "index.md", StringComparison.Ordinal))
        {
            throw new CommandException("the index is not a command", CommandErrno.NotPermitted);
        }

        if (!directory)
        {
            throw new CommandException($"'{name}' is a directory", CommandErrno.IsDirectory);
        }

        // Removing the directory takes the command whatever is still in it, and kills it if it is
        // running: the directory is the only handle a caller has on it, so leaving the process
        // alive would leave it running for ever with its output going nowhere.
        registry.RemoveTree(name);
    }

    private CommandDirectory Directory(Command command) =>
        new(registry, command, waitTimeout);
}
