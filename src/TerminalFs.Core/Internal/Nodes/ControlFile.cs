namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/ctl/&lt;name&gt;</c>: a name taken for a command, and the file that command is written to.
/// </summary>
/// <remarks>
/// <para>
/// The node stands for a <see cref="Draft"/> — a name somebody has taken and not yet written a
/// command for. A name nobody has taken is not a file here, which is what lets a client create
/// one: while every valid name resolved on a walk, an exclusive create could only ever answer
/// "file exists", and a client that makes its files before writing to them could not use this
/// tree at all.
/// </para>
/// <para>
/// <paramref name="claiming" /> marks the node the create returns, which is handed only to the
/// fid that made it. A create and the open that follows are two messages, and without this
/// another caller could open a name in between and win one they never made.
/// </para>
/// </remarks>
internal sealed class ControlFile : TerminalControl
{
    private readonly CommandRegistry registry;
    private readonly Draft draft;
    private readonly bool claiming;

    internal ControlFile(CommandRegistry registry, Draft draft, bool claiming = false)
        : base(draft.Name, draft.Key)
    {
        this.registry = registry;
        this.draft = draft;
        this.claiming = claiming;
    }

    /// <inheritdoc />
    public override uint Revision => draft.Revision;

    /// <inheritdoc />
    public override DateTimeOffset? ModifiedAt => draft.ChangedAt;

    /// <inheritdoc />
    public override int Length => draft.Length;

    /// <inheritdoc />
    public override ControlSession Open() => registry.OpenControl(draft, claiming);

    /// <inheritdoc />
    public override void Rename(string newName) => registry.RenameDraft(draft.Name, newName);
}
