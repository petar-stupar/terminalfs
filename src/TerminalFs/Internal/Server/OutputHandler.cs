using NineP.Protocol;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>
/// Serves <c>stdout</c> or <c>stderr</c>: a file that grows while it is being read.
/// </summary>
/// <remarks>
/// <para>
/// A read past what has arrived answers nothing rather than waiting for more. That is what makes
/// these ordinary files: <c>cat</c> ends, <c>grep</c> ends, <c>wc -l</c> ends, and reading again
/// a moment later picks up what has arrived since. A read that blocked until the command finished
/// would make every one of those hang on a command that runs for an hour, and the caller could
/// not tell that from a wedged mount. Waiting is what <c>wait</c> is for, and it is a separate
/// file so that the choice is the caller's.
/// </para>
/// <para>
/// The size is never larger than what a read will produce, which is why the length is raised only
/// after the bytes are flushed: a client stops at the first short read and calls it the end.
/// </para>
/// </remarks>
internal sealed class OutputHandler(TerminalOutput output, TerminalTree tree) : IFileHandler
{
    /// <inheritdoc />
    public Qid Qid => tree.QidOf(output);

    /// <inheritdoc />
    public ValueTask<Attr> GetAttrAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TerminalAttributes.Of(
            Qid,
            FileKind.File,
            TerminalAttributes.PageMode,
            (ulong)output.File.Length,
            tree.TimeOf(output)));

    /// <inheritdoc />
    public ValueTask<IOpenFile> OpenAsync(
        OpenMode mode,
        OpenFlags flags,
        CancellationToken cancellationToken = default)
    {
        Command.Lease? lease = output.Lease();

        try
        {
            return ValueTask.FromResult<IOpenFile>(new Open(output.File, lease));
        }
        catch (CommandException refusal)
        {
            lease?.Dispose();

            throw TerminalTree.Refused(refusal);
        }
    }

    /// <inheritdoc />
    public ValueTask SetAttrAsync(SetAttr update, CancellationToken cancellationToken = default) =>
        throw new NinePException(NinePError.FromErrno(Errno.EROFS));

    /// <inheritdoc />
    public ValueTask ClunkAsync(bool wasOpen, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask FsyncAsync(bool dataOnly, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    /// <summary>One open of one output stream.</summary>
    private sealed class Open : IOpenFile
    {
        private readonly OutputFile file;
        private readonly Command.Lease? lease;
        private readonly Stream stream;
        private readonly SemaphoreSlim gate = new(1, 1);

        internal Open(OutputFile file, Command.Lease? lease)
        {
            this.file = file;
            this.lease = lease;
            stream = file.OpenRead();
        }

        /// <summary>
        /// Reads from where the caller asked.
        /// </summary>
        /// <remarks>
        /// The stream is seeked rather than followed, because 9P reads carry their own offset and
        /// nothing says they arrive in order. The gate is here because one fid can have several
        /// reads in flight and a seek followed by a read is two steps.
        /// </remarks>
        public async ValueTask<int> ReadAsync(
            ulong offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            long length = file.Length;

            if (offset >= (ulong)length)
            {
                return 0;
            }

            int wanted = (int)Math.Min((ulong)buffer.Length, (ulong)length - offset);

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                stream.Seek((long)offset, SeekOrigin.Begin);

                return await stream.ReadAsync(buffer[..wanted], cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <inheritdoc />
        public ValueTask<int> WriteAsync(
            ulong offset,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            throw new NinePException(NinePError.FromErrno(Errno.EROFS));

        /// <inheritdoc />
        public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult((ulong)file.Length);

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);

            gate.Dispose();
            lease?.Dispose();
        }
    }
}
