namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/ctl</c>: a file per name somebody is writing a command for.
/// </summary>
/// <remarks>
/// <para>
/// A name nobody has taken is not a file here. That is what lets a client make one — and a client
/// that makes its files before it writes to them is the ordinary case, not an exotic one. While
/// every syntactically valid name resolved on a walk there was never anything left to create, so
/// an exclusive create could only ever answer "file exists", and an agent harness whose write
/// tool opens a temporary file with <c>O_EXCL</c> could not use this tree at all.
/// </para>
/// <para>
/// So a name here is a <see cref="Draft"/>, and the listing holds the drafts: a held name is a
/// real file that can be created, stat'd, written, listed, removed and renamed. A directory that
/// hid its own entries could not be <c>ls</c>'d, and a name nobody could see would be a name
/// nobody could clean up.
/// </para>
/// <para>
/// A name that has already run is not here either — it is a directory under <c>/cmd</c>, which is
/// the honest answer to where it went. The refusal a caller needs, that the name is taken, now
/// comes from the create that asked for it rather than from an open of something pretending to
/// be there.
/// </para>
/// <para>
/// Nothing in here decides a draft. This is where a name is still being decided, and a listing of
/// the names in flight is not a reason to run any of them; only an operation that asks about
/// <c>/cmd</c> settles one.
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
    public override DateTimeOffset? ModifiedAt => registry.ChangedAt;

    /// <inheritdoc />
    public override IReadOnlyList<TerminalNode> Children =>
        [index, .. registry.Drafts.Select(draft => new ControlFile(registry, draft))];

    /// <inheritdoc />
    public override TerminalNode? Find(string name)
    {
        if (string.Equals(name, "index.md", StringComparison.Ordinal))
        {
            return index;
        }

        return registry.FindDraft(name) is { } draft ? new ControlFile(registry, draft) : null;
    }

    /// <summary>
    /// Takes a name for a command that has not been written yet.
    /// </summary>
    /// <remarks>
    /// The node this answers with holds the open the create is entitled to, and is handed only to
    /// the fid that made it. Two callers racing for one unused name therefore still resolve to
    /// one winner and one refusal, at the moment they collide, which is what the open used to do.
    /// </remarks>
    public override TerminalNode Create(string name)
    {
        if (string.Equals(name, "index.md", StringComparison.Ordinal))
        {
            throw new CommandException(
                "'index.md' is this directory's own page",
                CommandErrno.Exists);
        }

        return new ControlFile(registry, registry.CreateDraft(name), claiming: true);
    }

    /// <summary>
    /// Frees a name nothing has been written for.
    /// </summary>
    /// <remarks>
    /// A name that has run is not here to remove: it is a command, and a command is removed at
    /// its own directory, which is where its output and its exit code are.
    /// </remarks>
    public override void Remove(string name, bool directory)
    {
        if (string.Equals(name, "index.md", StringComparison.Ordinal))
        {
            throw new CommandException("the index is not a command", CommandErrno.NotPermitted);
        }

        if (directory)
        {
            throw new CommandException($"'{name}' is not a directory", CommandErrno.NotDirectory);
        }

        registry.RemoveDraft(name);
    }

    /// <summary>
    /// Gives a name that has not run yet another name.
    /// </summary>
    /// <remarks>
    /// This is what a client that writes to a temporary file and renames it into place needs, and
    /// it is nothing more than a rename: the draft it moves has not been decided, or has been
    /// decided and not yet run, so moving it moves the name a command will run under rather than
    /// doing anything to a command.
    /// </remarks>
    public override void Rename(string name, TerminalDirectory destination, string newName)
    {
        ArgumentNullException.ThrowIfNull(destination);

        // A draft belongs to /ctl. Nothing else in this tree takes one, and a caller moving one
        // out is asking for something that has no meaning rather than something refused.
        if (!string.Equals(destination.Key, Key, StringComparison.Ordinal))
        {
            throw new CommandException(
                "a command being written can only be renamed within /ctl",
                CommandErrno.CrossDevice);
        }

        if (string.Equals(name, "index.md", StringComparison.Ordinal)
            || string.Equals(newName, "index.md", StringComparison.Ordinal))
        {
            throw new CommandException(
                "the index is not a command",
                CommandErrno.NotPermitted);
        }

        registry.RenameDraft(name, newName);
    }
}
