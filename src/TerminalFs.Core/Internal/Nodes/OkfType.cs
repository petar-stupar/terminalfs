namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// The <c>type</c> a page's frontmatter carries, which is the one field the Open Knowledge Format
/// requires. Named in one place so that a reader meets the same word for the same kind of thing
/// everywhere in the tree.
/// </summary>
internal static class OkfType
{
    internal static string Of(TerminalNodeKind kind) => kind switch
    {
        TerminalNodeKind.Root => "Terminal",
        TerminalNodeKind.Commands => "Command List",
        TerminalNodeKind.Command => "Command",
        TerminalNodeKind.Field => "Command Detail",
        TerminalNodeKind.Control => "Command Channel",
        _ => "Agent Skill",
    };
}
