namespace TerminalFs.Core;

/// <summary>
/// The file whose read does not answer until the command has stopped.
/// </summary>
/// <remarks>
/// <para>
/// This is the one thing in the tree that is not a plain file, and it exists because polling is
/// the alternative. Without it an agent watching a command re-reads <c>status</c> in a loop,
/// which costs a request every time round and still answers late.
/// </para>
/// <para>
/// The read is bounded rather than open-ended. A blocked read holds a request slot on the
/// connection for as long as it lasts, and on a mount it holds one of the bridge's workers too;
/// past a client's own timeout it stops being a command still running and becomes a filesystem
/// that has stopped answering. So a wait that reaches the limit answers <c>running</c>, which is
/// true, and a caller who is still interested reads it again.
/// </para>
/// </remarks>
public sealed class TerminalWait : TerminalNode
{
    /// <summary>
    /// The width every answer is padded to.
    /// </summary>
    /// <remarks>
    /// A fixed width because the size has to be stated before the answer is known. A client that
    /// clamps a read to the size it was told — which is what a stat is for — would read nothing
    /// at all from a file that said it was empty, and a file that said it was longer than its
    /// answer would leave the client waiting for bytes that never come.
    /// </remarks>
    public const int Width = 10;

    private readonly Command command;

    internal TerminalWait(string key, Command command, TimeSpan timeout)
        : base("wait", TerminalNodeKind.Wait, key)
    {
        this.command = command;
        Timeout = timeout;
    }

    /// <summary>How long a read waits before answering <c>running</c>.</summary>
    public TimeSpan Timeout { get; }

    /// <inheritdoc />
    public override uint Revision => command.StatusRevision;

    /// <inheritdoc />
    public override DateTimeOffset? ModifiedAt => command.ChangedAt;

    /// <inheritdoc />
    public override Command.Lease? Lease() => command.Open();

    /// <summary>Waits for the command to stop, and answers with where it got to.</summary>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken)
    {
        CommandState state = await command.WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);

        string line = state switch
        {
            CommandState.Completed => "completed",
            CommandState.Error => "error",
            _ => "running",
        };

        return System.Text.Encoding.UTF8.GetBytes(line.PadRight(Width - 1) + "\n");
    }
}
