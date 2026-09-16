namespace TerminalFs.Core;

/// <summary>A readable file of the served tree.</summary>
public abstract class TerminalPage : TerminalNode
{
    private protected TerminalPage(string name, TerminalNodeKind kind, string key)
        : base(name, kind, key)
    {
    }

    /// <summary>The bytes a read returns.</summary>
    public abstract ValueTask<ReadOnlyMemory<byte>> ContentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// How many bytes <see cref="ContentAsync"/> returns.
    /// </summary>
    /// <remarks>
    /// A client stats a file before reading it and the two answers must agree, so a page that can
    /// change between the stat and the read must never state a size larger than what the read
    /// will produce: the client stops at the first short read and calls that the end of the file.
    /// </remarks>
    public virtual async ValueTask<ulong> SizeAsync(CancellationToken cancellationToken = default) =>
        (ulong)(await ContentAsync(cancellationToken).ConfigureAwait(false)).Length;
}
