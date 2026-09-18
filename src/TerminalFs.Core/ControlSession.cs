using System.Buffers;
using System.Text;

namespace TerminalFs.Core;

/// <summary>
/// One open of one command's control file, from the first byte written to the close that runs it.
/// </summary>
/// <remarks>
/// <para>
/// One command per open, not one per line. A command may be several lines — a loop, a heredoc, a
/// script — and a line-per-command protocol could not carry one. The open is the boundary a
/// caller already has: <c>echo … &gt; /ctl/t1</c> and a heredoc both open, write and close.
/// </para>
/// <para>
/// The id is the file's name rather than a word inside it, and that is what makes two callers
/// writing at once work. It was a header once, with every command going through a single
/// <c>/ctl</c>; a mount then showed why it cannot be. Four processes writing to one path do not
/// reach the server as four writes — macOS smbfs merges them in its page cache and sends one,
/// and the other three commands are lost with no error anywhere. Separate names are separate
/// files, and a client has nothing to merge.
/// </para>
/// <para>
/// Offsets are ignored. A control file is a command channel rather than a byte range, which is
/// the Plan 9 convention and also the only workable reading: a client that seeks in it is not
/// addressing anything.
/// </para>
/// </remarks>
public sealed class ControlSession : IDisposable
{
    private readonly CommandRegistry registry;
    private readonly Command command;
    private readonly int maxBytes;
    private readonly ArrayBufferWriter<byte> pending = new();

    private bool done;
    private CommandException? refusal;

    internal ControlSession(CommandRegistry registry, Command command, int maxBytes)
    {
        this.registry = registry;
        this.command = command;
        this.maxBytes = maxBytes;
    }

    /// <summary>The command this open is writing.</summary>
    public Command Command => command;

    /// <summary>Takes bytes written to this open.</summary>
    /// <exception cref="CommandException">
    /// What has been written cannot be run. The write fails, which is where a caller sees that
    /// something went wrong; on 9P2000.L, which is what a mount speaks, only the error number
    /// survives, so the reason is put on the command itself, at <c>/cmd/&lt;id&gt;/reason</c>.
    /// </exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        // A client that retries a failed write must be told the same thing again. A mount does
        // exactly this, and answering the second attempt with something about the state this
        // session is now in would describe the machinery rather than the mistake.
        if (refusal is not null)
        {
            throw new CommandException(refusal.Message, refusal.Errno);
        }

        try
        {
            Accept(data);
        }
        catch (CommandException refused)
        {
            refusal = refused;

            // Marked here rather than at the close so that the directory holding the reason is
            // there the moment the write fails, which is when a caller goes looking. The text is
            // read out before the buffer is cleared: it is what `command` reports.
            registry.Deny(command, Encoding.UTF8.GetString(pending.WrittenSpan), refused.Message);
            pending.Clear();

            throw;
        }
    }

    /// <summary>
    /// Runs what was written. Called when the open is released, however it is released.
    /// </summary>
    /// <remarks>
    /// A disconnected client releases its fids too, so a command whose text arrived before the
    /// connection dropped still runs. That is deliberate: the caller asked for it, and the bytes
    /// were all there.
    /// </remarks>
    public void Close()
    {
        if (done)
        {
            return;
        }

        done = true;

        if (refusal is not null)
        {
            // Already denied, and it keeps its name: a name given back would take the reason with
            // it. A caller who rewords the command removes the directory, or picks another name.
            return;
        }

        registry.Start(command, Encoding.UTF8.GetString(pending.WrittenSpan));
    }

    /// <inheritdoc />
    public void Dispose() => Close();

    private void Accept(ReadOnlySpan<byte> data)
    {
        if (done)
        {
            throw new CommandException(
                "this file has already run its command; a name runs once");
        }

        if (pending.WrittenCount + data.Length > maxBytes)
        {
            throw new CommandException(
                $"a command may not be longer than {maxBytes} bytes",
                CommandErrno.TooLarge);
        }

        pending.Write(data);

        // Checked as it arrives so that the usual single-write command fails the write that
        // carried it. A command assembled over several writes can only reach a denied shape at
        // the end, and is checked again when the file closes.
        Refuse(Encoding.UTF8.GetString(pending.WrittenSpan));
    }

    private void Refuse(string text)
    {
        if (text.Trim().Length == 0)
        {
            return;
        }

        if (registry.Options.Deny.Match(text) is not { } rule)
        {
            return;
        }

        throw new CommandException(registry.Refusal(rule), CommandErrno.NotPermitted);
    }
}
