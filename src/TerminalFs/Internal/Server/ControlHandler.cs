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
    /// Reports a length of zero while the file can still be written, and what was written once it
    /// cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The zero is not a rounding-down of the truth: a client that believes a file has contents
    /// treats a write as a modification of them, reading what it thinks is there and sending back
    /// the merged result. When this file answered with help text, macOS smbfs laid the command
    /// over the front of it and sent the whole thing, so what ran was the command followed by the
    /// tail of its own help — a parse error in a line nobody wrote. A file with no length has
    /// nothing to merge.
    /// </para>
    /// <para>
    /// Once the name has been decided that hazard is gone, because it cannot be opened again by
    /// anyone. The length is then worth telling the truth about: a client that writes a file
    /// atomically stats it afterwards to check what it wrote, and a zero there is a write it
    /// reports as having silently failed — for a command that in fact ran.
    /// </para>
    /// </remarks>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TerminalAttributes.Of(
            Qid, FileKind.File, TerminalAttributes.ControlMode, (ulong)control.Length, tree.Started));

    /// <summary>
    /// Opens the file for writing a command into.
    /// </summary>
    /// <remarks>
    /// The name was taken by the create, which is where two callers racing for one resolve to a
    /// winner and an <c>EEXIST</c>. What is refused here is a second writer on a name that is
    /// already being written, and a name that has been written to and is waiting to run — so
    /// <c>echo a &gt; ctl/t1; echo b &gt; ctl/t1</c> still meets "a name runs once", and the
    /// <c>O_TRUNC</c> of that second redirect never reaches the file.
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
            // One of the few refusals with nowhere to leave a sentence: what it is about belongs
            // to somebody else's open, or to a command that is about to run. A mount reports only
            // "File exists", and `ls /cmd/<name>` is what says which.
            throw TerminalTree.Refused(refusal);
        }
    }

    /// <summary>
    /// Renames the file, accepts the truncation a shell redirect performs, and refuses everything
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A name arrives here rather than at the parent only on 9P2000 and <c>.u</c>, where a rename
    /// is a <c>Twstat</c> carrying one. A <c>.L</c> mount sends <c>Trenameat</c> to the directory
    /// instead, so both dialects reach the same place by different roads and neither can be the
    /// only one implemented.
    /// </para>
    /// <para>
    /// <c>echo cmd &gt; /ctl/t1</c> opens with <c>O_TRUNC</c>, which v9fs sends as a
    /// <c>Tsetattr</c> setting size to zero. Refusing it fails the open, so the documented way to
    /// use this file would not work on a mount at all. A command channel has no length to
    /// truncate, so the request is accepted and does nothing. A request that would change what
    /// the file *is* — its mode, owner or flags — is still refused.
    /// </para>
    /// </remarks>
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (update.Name is { } renamed)
        {
            try
            {
                control.Rename(renamed);
            }
            catch (CommandException refusal)
            {
                throw TerminalTree.Refused(refusal);
            }
        }

        if (update.Perm is not null
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
