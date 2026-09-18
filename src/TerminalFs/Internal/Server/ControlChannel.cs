using NineP.Protocol;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>
/// One open of one <c>/ctl/&lt;id&gt;</c>.
/// </summary>
/// <remarks>
/// The command is started when this is disposed, which the server does when the fid is clunked —
/// including when a client disconnects without clunking.
/// </remarks>
internal sealed class ControlChannel(ControlSession session) : IOpenFile
{
    /// <summary>
    /// Takes bytes written to this open.
    /// </summary>
    /// <remarks>
    /// The offset is ignored. A control file is a command channel rather than a byte range, which
    /// is the Plan 9 convention and also the only workable reading: a client seeking in it is not
    /// addressing anything.
    /// </remarks>
    public ValueTask<int> WriteAsync(
        ulong offset,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // The span aliases the connection's own frame buffer and is valid only for this
            // call, so the session copies what it keeps.
            session.Write(data.Span);
        }
        catch (CommandException refusal)
        {
            throw TerminalTree.Refused(refusal);
        }

        return ValueTask.FromResult(data.Length);
    }

    /// <summary>Answers nothing: there is nothing in this file to read.</summary>
    public ValueTask<int> ReadAsync(
        ulong offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0);

    /// <inheritdoc />
    public ValueTask<ulong> GetSizeAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0UL);

    /// <summary>
    /// Runs what was written.
    /// </summary>
    /// <remarks>
    /// 9P has no error on a clunk that a write's caller would see, so a command that cannot start
    /// is not refused here: it is recorded on the command itself. One the rules would not allow is
    /// denied, with the reason in its <c>reason</c> file; one whose shell would not start is an
    /// error, with the reason on its <c>stderr</c>.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        try
        {
            session.Close();
        }
        catch (CommandException refusal)
        {
            Diagnostics.Report($"closing /ctl/{session.Command.Id}: {refusal.Message}");
        }

        return ValueTask.CompletedTask;
    }
}
