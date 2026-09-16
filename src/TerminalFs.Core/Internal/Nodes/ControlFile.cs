namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/ctl/&lt;id&gt;</c>: writing a command here runs it under that name.
/// </summary>
/// <remarks>
/// The node is built on the way past rather than kept, because until someone opens it there is
/// no command and nothing to keep. The id is taken at the open, which is what makes two callers
/// racing for the same name resolve to one winner and one <c>EEXIST</c>.
/// </remarks>
internal sealed class ControlFile : TerminalControl
{
    private readonly CommandRegistry registry;

    internal ControlFile(CommandRegistry registry, string id)
        : base(id, "/ctl/" + id) => this.registry = registry;

    /// <inheritdoc />
    public override ControlSession Open() => registry.OpenControl(Name);
}
