namespace TerminalFs.Core;

/// <summary>What a node of the served tree stands for.</summary>
public enum TerminalNodeKind
{
    /// <summary>The root of the tree.</summary>
    Root,

    /// <summary>The control file.</summary>
    Control,

    /// <summary>The directory holding every command.</summary>
    Commands,

    /// <summary>One command's directory.</summary>
    Command,

    /// <summary>A file describing one thing about a command.</summary>
    Field,

    /// <summary>One of a command's output streams.</summary>
    Output,

    /// <summary>The file whose read blocks until a command stops.</summary>
    Wait,

    /// <summary>A page about how to use this tree.</summary>
    Skill,
}

/// <summary>
/// One entry of the served tree.
/// </summary>
/// <remarks>
/// These types are the whole of what the 9P layer above sees. A node says what it is called, what
/// it stands for, when it last changed and how many times it has changed; the layer above turns
/// that into a qid, an attribute record and a handler. Neither half needs the other's vocabulary.
/// </remarks>
public abstract class TerminalNode
{
    private protected TerminalNode(string name, TerminalNodeKind kind, string key)
    {
        Name = name;
        Kind = kind;
        Key = key;
    }

    /// <summary>The single path element naming this node.</summary>
    public string Name { get; }

    /// <summary>What this node stands for.</summary>
    public TerminalNodeKind Kind { get; }

    /// <summary>
    /// A stable identifier, unique within one tree, from which the 9P layer derives a qid path.
    /// Stability is what lets a client hold a walked fid across a change to what is behind it.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Moves whenever what is at this path changes.
    /// </summary>
    /// <remarks>
    /// A path keeps its qid path for as long as it is the same place, so the version is the only
    /// field that can tell a caching client that what is at it has changed. Left at zero, a
    /// client that caches on the qid goes on serving bytes it read minutes ago — which for a
    /// growing <c>stdout</c> or a <c>status</c> that has moved to <c>completed</c> is the whole
    /// of what was being asked for.
    /// </remarks>
    public virtual uint Revision => 0;

    /// <summary>When this node last changed, or null to report the time the server started.</summary>
    public virtual DateTimeOffset? ModifiedAt => null;

    /// <summary>
    /// Takes a handle on whatever this node belongs to, so it is not removed while it is open.
    /// Null when nothing is holding anything.
    /// </summary>
    public virtual Command.Lease? Lease() => null;
}
