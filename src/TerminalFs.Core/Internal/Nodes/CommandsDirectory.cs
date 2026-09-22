namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/cmd</c>: one directory per command, and an index naming them all.
/// </summary>
/// <remarks>
/// <para>
/// This is also where a draft becomes a command. A name that has been written to and closed does
/// not run at once — it settles, so that a client which writes to a temporary name and renames it
/// into place gets its command under the name it meant. Waiting out that window would mean
/// <c>echo … &gt; ctl/t1; cat cmd/t1/wait</c> found nothing, so anything here that would reveal
/// whether a command exists settles it first, and the clock is only the backstop for one nobody
/// looks at.
/// </para>
/// <para>
/// Only an operation that asks about <c>/cmd</c> decides a draft; <c>/ctl</c> is where a name is
/// still being decided, so nothing there decides it. Within here the rule is that <b>a page that
/// names a command settles it, and a page that counts them does not</b> — which is why the
/// listing and the index settle every decided draft and <c>/index.md</c>, which only counts,
/// settles none. A lookup settles the one name it asks for and no other: settling every draft on
/// any lookup would mean one caller reading <c>cmd/other/stdout</c> spawned somebody else's
/// half-written temporary file.
/// </para>
/// </remarks>
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
            () =>
            {
                // The index names every command and links to each one's status, so a link that
                // led nowhere would be worse than a page that took a moment to render.
                registry.Settle();

                return TreeText.CommandsIndex(registry);
            },
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
    public override IReadOnlyList<TerminalNode> Children
    {
        get
        {
            // A listing that left out a name a walk would then find is a listing that contradicts
            // itself one message later.
            registry.Settle();

            return [index, .. registry.Commands.Select(Directory)];
        }
    }

    /// <inheritdoc />
    public override TerminalNode? Find(string name)
    {
        if (string.Equals(name, "index.md", StringComparison.Ordinal))
        {
            return index;
        }

        // The one trigger that matters: `cat cmd/t1/wait` is a walk whose second element is
        // `t1`, and it must not miss a command whose bytes have already been written.
        registry.Settle(name);

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
