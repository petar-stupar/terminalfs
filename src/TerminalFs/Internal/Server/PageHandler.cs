using NineP.Protocol;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>Serves one page as a read-only file.</summary>
internal sealed class PageHandler(TerminalPage page, TerminalTree tree) : IFileHandler
{
    /// <inheritdoc />
    public Qid Qid => tree.QidOf(page);

    /// <inheritdoc />
    public async ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default)
    {
        ulong size = await page.SizeAsync(cancellationToken).ConfigureAwait(false);

        return TerminalAttributes.Of(Qid, FileKind.File, TerminalAttributes.PageMode, size, tree.TimeOf(page));
    }

    /// <summary>
    /// Renders the page and holds its bytes for as long as this open lasts.
    /// </summary>
    /// <remarks>
    /// The bytes are held by the open rather than looked up per read, for two reasons. A page
    /// bigger than one <c>msize</c> arrives as several reads, and a page that renders afresh each
    /// time would render a different answer into the middle of the one a client is assembling —
    /// a <c>status</c> that changed between two chunks would be read as neither value. The open
    /// is the unit over which the answer has to hold still.
    /// </remarks>
    public async ValueTask<IOpenFile> OpenAsync(
        OpenMode mode,
        OpenFlags flags,
        CancellationToken cancellationToken = default) =>
        new Open(await page.ContentAsync(cancellationToken).ConfigureAwait(false), page.Lease());

    /// <inheritdoc />
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    /// <inheritdoc />
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>One open of one page: the rendered bytes, served from memory.</summary>
    private sealed class Open(ReadOnlyMemory<byte> content, Command.Lease? lease) : IOpenFile
    {
        /// <inheritdoc />
        public ValueTask<int> ReadAsync(
            ulong offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (offset >= (ulong)content.Length)
            {
                return ValueTask.FromResult(0);
            }

            int at = (int)offset;
            int count = Math.Min(buffer.Length, content.Length - at);

            content.Slice(at, count).CopyTo(buffer);

            return ValueTask.FromResult(count);
        }

        /// <inheritdoc />
        public ValueTask<int> WriteAsync(
            ulong offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EROFS));

        /// <inheritdoc />
        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((ulong)content.Length);

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            lease?.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
