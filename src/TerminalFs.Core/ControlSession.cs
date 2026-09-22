using System.Buffers;
using System.Text;

namespace TerminalFs.Core;

/// <summary>
/// One open of one draft's control file, from the first byte written to the close that decides it.
/// </summary>
/// <remarks>
/// <para>
/// One command per name, and at most one per open. A command may be several lines — a loop, a
/// heredoc, a script — and a line-per-command protocol could not carry one. The open is the
/// boundary a caller already has: <c>echo … &gt; /ctl/t1</c> and a heredoc both open, write and
/// close. An open that writes nothing decides nothing and leaves the name for the next one.
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
    private readonly Draft draft;
    private readonly int maxBytes;
    private readonly ArrayBufferWriter<byte> pending = new();

    private bool done;
    private CommandException? refusal;

    internal ControlSession(CommandRegistry registry, Draft draft, int maxBytes)
    {
        this.registry = registry;
        this.draft = draft;
        this.maxBytes = maxBytes;
    }

    /// <summary>The name this open is writing a command for.</summary>
    public Draft Draft => draft;

    /// <summary>Takes bytes written to this open.</summary>
    /// <exception cref="CommandException">
    /// What has been written cannot be run. The write fails, which is where a caller sees that
    /// something went wrong; on 9P2000.L, which is what a mount speaks, only the error number
    /// survives, so the reason is kept and ends up at <c>/cmd/&lt;id&gt;/reason</c>.
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

            // Decided here rather than at the close, because a refusal is final the moment it is
            // given: the caller already has the error, and nothing they write afterwards can
            // change it. The text is read out before the buffer is cleared — it is what
            // `command` will report.
            registry.Refuse(draft, Encoding.UTF8.GetString(pending.WrittenSpan), refused.Message);
            pending.Clear();

            throw;
        }
    }

    /// <summary>
    /// Decides the draft on what was written. Called when the open is released, however it is
    /// released.
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
            // Decided at the write, and on its clock since then. Nothing to do here, and the
            // draft may already have become a command while this fid was still open.
            return;
        }

        // Nothing at all was written, so there is nothing to run and nothing to refuse. The name
        // stays taken and the next open of it continues here: a client that creates a file before
        // writing to it — which is what an agent harness's write tool does — opens, closes, opens
        // again and writes, and the second close is the one that decides it.
        //
        // Zero bytes rather than nothing but whitespace. A caller who wrote a newline said
        // something, and what they said is not a command; a caller who wrote nothing never said
        // anything at all.
        if (pending.WrittenCount == 0)
        {
            draft.EndSession();

            return;
        }

        registry.Ready(draft, Encoding.UTF8.GetString(pending.WrittenSpan));
    }

    /// <inheritdoc />
    public void Dispose() => Close();

    private void Accept(ReadOnlySpan<byte> data)
    {
        if (done)
        {
            throw new CommandException(
                "this file has already been closed; a name runs once");
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
