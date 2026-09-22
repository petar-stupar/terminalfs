using NineP.Protocol;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>Serves one directory of the tree.</summary>
internal sealed class DirectoryHandler(TerminalDirectory directory, TerminalTree tree)
    : IDirectoryHandler, IStatFsCapability
{
    /// <summary>
    /// The entries this listing is walking, taken once when it began.
    /// </summary>
    /// <remarks>
    /// The cursor is an index, so the list it indexes has to hold still — and this is a tree in
    /// which it otherwise would not. A command started or removed between two pages of one
    /// readdir shifts every entry after it, and the client silently skips a name or sees one
    /// twice. A handler is per-fid, which is exactly the lifetime of one listing.
    /// </remarks>
    private IReadOnlyList<TerminalNode>? listing;

    /// <summary>
    /// The node this handler serves, so a rename can tell one directory from another.
    /// </summary>
    internal TerminalDirectory Node => directory;

    /// <inheritdoc />
    public Qid Qid => tree.QidOf(directory);

    /// <inheritdoc />
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TerminalAttributes.Of(
            Qid,
            FileKind.Directory,
            directory.Writable ? TerminalAttributes.WritableDirectoryMode : TerminalAttributes.DirectoryMode,
            0,
            tree.TimeOf(directory)));

    /// <inheritdoc />
    public ValueTask<IHandler?> LookupAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TerminalNode? child = directory.Find(name);

        return ValueTask.FromResult(child is null ? null : tree.HandlerFor(child));
    }

    /// <inheritdoc />
    public ValueTask<DirectoryListing> ReadDirAsync(
        ulong cursor,
        int max,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The cursor is the count already delivered, so a listing resumed after a clunk and a
        // re-walk lands in the same place as one read straight through. Starting over takes a
        // fresh snapshot; continuing keeps the one the listing began with.
        IReadOnlyList<TerminalNode> children = cursor == 0
            ? listing = directory.Children
            : listing ??= directory.Children;

        int at = cursor > (ulong)children.Count ? children.Count : (int)cursor;
        var page = new List<DirEntry>(Math.Min(max, children.Count - at));

        while (at < children.Count && page.Count < max)
        {
            TerminalNode child = children[at];
            at++;

            page.Add(new DirEntry(
                child.Name,
                tree.QidOf(child),
                child is TerminalDirectory ? FileKind.Directory : FileKind.File,
                (ulong)at));
        }

        return ValueTask.FromResult(new DirectoryListing(page, (ulong)at, at >= children.Count));
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string name, FileKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            directory.Remove(name, kind == FileKind.Directory);
        }
        catch (CommandException refusal)
        {
            throw TerminalTree.Refused(refusal);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    /// <summary>
    /// Makes a child, which is how a name under <c>/ctl</c> is taken.
    /// </summary>
    /// <remarks>
    /// This is the main path to a command now: a name nobody has taken does not resolve on a
    /// walk, so a client opening <c>/ctl/&lt;id&gt;</c> with <c>O_CREAT</c> — which every shell
    /// redirect does — arrives here. Everywhere else in the tree refuses, because a command is
    /// made by writing one and an empty <c>/cmd/&lt;id&gt;</c> would have no command in it and
    /// no way to be given one.
    /// </remarks>
    public ValueTask<IHandler> CreateAsync(CreateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // Nothing here can be append-only, exclusive or temporary, and the core reads the new
        // file's attributes back and removes a file that lacks the flags its create asked for.
        // Refusing now rather than letting it do that means the name is never taken and given
        // back — a round trip in which a listing could see a name that was never real. Only a
        // legacy Tcreate can ask; a .L mount always sends none.
        if (request.FileFlags is not FileFlags.None)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EOPNOTSUPP));
        }

        if (request.Kind is not FileKind.File)
        {
            throw new NinePException(new NinePError(
                "a command is a file, not a directory", Errno.EPERM));
        }

        // request.Perm is ignored: a control file's mode is fixed, and the zero length that mode
        // exists alongside is what keeps a client from merging its own cache into a command.
        try
        {
            return ValueTask.FromResult(tree.HandlerFor(directory.Create(request.Name)));
        }
        catch (CommandException refusal)
        {
            throw TerminalTree.Refused(refusal);
        }
    }

    /// <summary>
    /// Moves a child, which is how a name being written to is given its final one.
    /// </summary>
    /// <remarks>
    /// A client that writes to a temporary file and renames it into place is the reason this
    /// exists. It is not a route to running anything — the draft it moves has not run — and a
    /// directory that has no drafts in it refuses, which is every directory but <c>/ctl</c>.
    /// </remarks>
    public ValueTask RenameAsync(
        string oldName,
        IDirectoryHandler newParent,
        string newName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The server builds a fresh handler for every walk, so reference equality on newParent
        // says nothing at all about which directory it is. The node behind it is the directory,
        // and the node's key is what names it.
        if (newParent is not DirectoryHandler destination)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EXDEV));
        }

        try
        {
            directory.Rename(oldName, destination.Node, newName);
        }
        catch (CommandException refusal)
        {
            throw TerminalTree.Refused(refusal);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Answers <c>Tstatfs</c>. This is not decoration: a tree that cannot say how much space it
    /// has cannot be re-exported. Samba calls <c>disk_free</c> when a client connects to a share,
    /// and a 9P mount that answers EOPNOTSUPP makes it fail the connect, which reaches macOS as
    /// "Operation not supported" and no mount at all.
    /// </summary>
    public ValueTask<StatFs> StatFsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new StatFs(
            Type: StatFs.V9fsMagic,
            BlockSize: 4096,
            Blocks: 1,
            BlocksFree: 0,
            BlocksAvailable: 0,
            Files: 1,
            FilesFree: 0,
            FsId: 0,
            NameLength: 255));

    /// <inheritdoc />
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
