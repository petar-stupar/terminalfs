using NineP.Protocol;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>
/// Serves <c>/ctl/&lt;id&gt;</c>: a file a command is written to.
/// </summary>
internal sealed class ControlHandler(TerminalControl control, TerminalTree tree) : IFileHandler
{
    /// <inheritdoc />
    public Qid Qid => tree.QidOf(control);

    /// <summary>
    /// Reports a length of zero, because there is nothing here to read.
    /// </summary>
    /// <remarks>
    /// Not a rounding-down of the truth: a client that believes a file has contents treats a
    /// write as a modification of them, reading what it thinks is there and sending back the
    /// merged result. When this file answered with help text, macOS smbfs laid the command over
    /// the front of it and sent the whole thing, so what ran was the command followed by the tail
    /// of its own help — a parse error in a line nobody wrote. A file with no length has nothing
    /// to merge.
    /// </remarks>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TerminalAttributes.Of(
            Qid, FileKind.File, TerminalAttributes.ControlMode, 0, tree.Started));

    /// <summary>
    /// Opens the file, which is what takes the name.
    /// </summary>
    /// <remarks>
    /// The id is taken here rather than when the command starts, so that two callers racing for
    /// one name resolve to a winner and an <c>EEXIST</c> at the moment they collide rather than
    /// after both have written.
    /// </remarks>
    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode,
        OpenFlags flags,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return ValueTask.FromResult<IOpenFile>(new ControlChannel(control.Open()));
        }
        catch (CommandException refusal)
        {
            // This is the one refusal with nowhere to leave a sentence: the name belongs to a
            // command that already exists, so there is no command of ours to write it on. A mount
            // reports only "File exists" — and the directory at /cmd/<name> is the reason, which
            // is why that is enough.
            throw TerminalTree.Refused(refusal);
        }
    }

    /// <summary>
    /// Accepts the truncation a shell redirect performs, and refuses everything else.
    /// </summary>
    /// <remarks>
    /// <c>echo cmd &gt; /ctl/t1</c> opens with <c>O_TRUNC</c>, which v9fs sends as a
    /// <c>Tsetattr</c> setting size to zero. Refusing it fails the open, so the documented way to
    /// use this file would not work on a mount at all. A command channel has no length to
    /// truncate, so the request is accepted and does nothing. A request that would change what
    /// the file *is* — its name, mode, owner or flags — is still refused.
    /// </remarks>
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
    {
        if (update.Name is not null
            || update.Perm is not null
            || update.Flags is not null
            || update.Uid is not null
            || update.Gid is not null
            || update.GroupName is not null)
        {
            throw new NinePException(NinePError.FromErrno(Errno.EROFS));
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
