namespace TerminalFs.Core.Internal.Nodes;

/// <summary>A directory whose children are settled when it is built.</summary>
internal sealed class StaticDirectory(
    string name,
    TerminalNodeKind kind,
    string key,
    IReadOnlyList<TerminalNode> children)
    : TerminalDirectory(name, kind, key)
{
    /// <inheritdoc />
    public override IReadOnlyList<TerminalNode> Children { get; } = children;
}
