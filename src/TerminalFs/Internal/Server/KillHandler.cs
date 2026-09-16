using NineP.Protocol;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>
/// Serves <c>/cmd/&lt;id&gt;/kill</c>: writing anything to it ends the command.
/// </summary>
/// <remarks>
/// A file per command rather than one shared one, for the same reason the control files are:
/// concurrent writes to a single path are merged by the client and arrive as one, so a shared
/// kill file would silently drop all but one of several kills issued at once.
/// </remarks>
internal sealed class KillHandler(TerminalKill kill, TerminalTree tree) : IFileHandler
{
    /// <inheritdoc />
    public Qid Qid => tree.QidOf(kill);

    /// <summary>Reports no length, as every file here that takes a command does.</summary>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TerminalAttributes.Of(
            Qid, FileKind.File, TerminalAttributes.ControlMode, 0, tree.TimeOf(kill)));

    /// <inheritdoc />
    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode,
        OpenFlags flags,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IOpenFile>(new Open(kill, kill.Lease()));

    /// <inheritdoc />
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

    /// <summary>One open of one kill file.</summary>
    private sealed class Open(TerminalKill kill, Command.Lease? lease) : IOpenFile
    {
        /// <summary>
        /// Ends the command. What was written is not read: there is one thing this file does, and
        /// a caller who opened it for writing has already said they want it.
        /// </summary>
        public ValueTask<int> WriteAsync(
            ulong offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            kill.Kill();

            return ValueTask.FromResult(data.Length);
        }

        /// <inheritdoc />
        public ValueTask<int> ReadAsync(
            ulong offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        /// <inheritdoc />
        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0UL);

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lease?.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
