namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/ctl</c>: a file per command, named by its id.
/// </summary>
/// <remarks>
/// <para>
/// The listing holds only the index, because there is nothing else to list: a name here is a
/// command that has not been written yet, and every valid unused name is available. A walk to
/// one succeeds whether or not anyone has mentioned it before, so <c>echo … &gt; /ctl/t1</c>
/// needs no create.
/// </para>
/// <para>
/// A name already taken resolves too, and is refused when it is opened. Refusing it at the walk
/// instead would answer "no such file", which is both untrue and the opposite of the truth: the
/// name is unavailable precisely because it exists. The refusal a caller needs is the one that
/// says so.
/// </para>
/// </remarks>
internal sealed class ControlDirectory : TerminalDirectory
{
    private readonly CommandRegistry registry;
    private readonly TerminalPage index;

    internal ControlDirectory(CommandRegistry registry, DateTimeOffset builtAt)
        : base("ctl", TerminalNodeKind.Control, "/ctl")
    {
        this.registry = registry;

        index = new TextPage(
            "index.md",
            TerminalNodeKind.Control,
            "/ctl/index.md",
            () => TreeText.ControlIndex(builtAt));
    }

    /// <inheritdoc />
    public override bool Writable => true;

    /// <inheritdoc />
    public override uint Revision => registry.Revision;

    /// <inheritdoc />
    public override IReadOnlyList<TerminalNode> Children => [index];

    /// <inheritdoc />
    public override TerminalNode? Find(string name)
    {
        if (string.Equals(name, "index.md", StringComparison.Ordinal))
        {
            return index;
        }

        return CommandId.IsValid(name) ? new ControlFile(registry, name) : null;
    }

    /// <summary>
    /// Refuses a removal. There is nothing here to remove: a name that has been used is a command
    /// under <c>/cmd</c>, and one that has not is not a file anybody made.
    /// </summary>
    public override void Remove(string name, bool directory) =>
        throw new CommandException(
            "nothing in /ctl can be removed; a command is removed at /cmd/<id>",
            CommandErrno.NotPermitted);
}
