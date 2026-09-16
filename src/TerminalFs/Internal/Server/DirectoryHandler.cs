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
    /// Refuses a create. A command is made by writing to <c>/ctl</c>, not by making a directory:
    /// an empty <c>/cmd/&lt;id&gt;</c> would have no command in it and no way to be given one.
    /// </summary>
    public ValueTask<IHandler> CreateAsync(CreateRequest request, CancellationToken cancellationToken = default) =>
        throw new NinePException(new NinePError(
            "a command is made by writing 'run <id> <command>' to /ctl", Errno.EPERM));

    /// <inheritdoc />
    public ValueTask RenameAsync(
        string oldName,
        IDirectoryHandler newParent,
        string newName,
        CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

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
