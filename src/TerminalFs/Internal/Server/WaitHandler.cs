using NineP.Protocol;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>
/// Serves <c>wait</c>: a read that does not answer until the command has stopped.
/// </summary>
/// <remarks>
/// <para>
/// The server dispatches requests on one connection concurrently and serialises only per fid, so
/// a read blocked here does not hold up a write to <c>/ctl</c> or a read of another command's
/// output. It does hold one of the connection's request slots for as long as it lasts, which is
/// the reason the wait is bounded rather than open-ended.
/// </para>
/// <para>
/// A flushed read cancels the token, so <c>Ctrl-C</c> on a <c>cat</c> of this file stops the wait
/// rather than leaving it to finish into a reply nobody will receive.
/// </para>
/// </remarks>
internal sealed class WaitHandler(TerminalWait wait, TerminalTree tree) : IFileHandler
{
    /// <inheritdoc />
    public Qid Qid => tree.QidOf(wait);

    /// <summary>
    /// The size is fixed, and every answer is padded to it.
    /// </summary>
    /// <remarks>
    /// The size has to be stated before the answer is known. A client that clamps a read to the
    /// size it was told — which is what a stat is for — would read nothing from a file that said
    /// it was empty, and one that said it was longer than its answer would leave the client
    /// waiting for bytes that never come.
    /// </remarks>
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TerminalAttributes.Of(
            Qid, FileKind.File, TerminalAttributes.PageMode, TerminalWait.Width, tree.TimeOf(wait)));

    /// <inheritdoc />
    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode,
        OpenFlags flags,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IOpenFile>(new Open(wait, wait.Lease()));

    /// <inheritdoc />
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    /// <inheritdoc />
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>One open of the wait file.</summary>
    private sealed class Open(TerminalWait wait, Command.Lease? lease) : IOpenFile
    {
        private ReadOnlyMemory<byte>? answer;

        /// <summary>
        /// Waits, then answers. A second read of the same open returns the same answer rather
        /// than waiting again: <c>cat</c> reads until it is given nothing, and waiting a second
        /// time would double every wait.
        /// </summary>
        public async ValueTask<int> ReadAsync(
            ulong offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            answer ??= await wait.ReadAsync(cancellationToken).ConfigureAwait(false);

            ReadOnlyMemory<byte> content = answer.Value;

            if (offset >= (ulong)content.Length)
            {
                return 0;
            }

            int at = (int)offset;
            int count = Math.Min(buffer.Length, content.Length - at);

            content.Slice(at, count).CopyTo(buffer);

            return count;
        }

        /// <inheritdoc />
        public ValueTask<int> WriteAsync(
            ulong offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EROFS));

        /// <inheritdoc />
        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((ulong)TerminalWait.Width);

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lease?.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
