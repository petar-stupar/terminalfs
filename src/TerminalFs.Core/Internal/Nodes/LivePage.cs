using System.Text;

namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// A page whose text is produced afresh every time it is asked for.
/// </summary>
/// <remarks>
/// <c>status</c> moves from <c>running</c> to <c>completed</c>, <c>exitcode</c> appears, and the
/// list of commands changes as they come and go — so nothing here may be cached, and the
/// revision it carries is what tells a caching client the same path now holds something else.
/// </remarks>
internal sealed class LivePage(
    string name,
    TerminalNodeKind kind,
    string key,
    Func<string> render,
    Func<uint> revision,
    Func<DateTimeOffset>? modifiedAt = null,
    Func<Command.Lease?>? lease = null)
    : TerminalPage(name, kind, key)
{
    /// <inheritdoc />
    public override uint Revision => revision();

    /// <inheritdoc />
    public override DateTimeOffset? ModifiedAt => modifiedAt?.Invoke();

    /// <inheritdoc />
    public override Command.Lease? Lease() => lease?.Invoke();

    /// <inheritdoc />
    public override ValueTask<ReadOnlyMemory<byte>> ContentAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(render()));
}
