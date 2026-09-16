using System.Text;

namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// A page whose text never changes, rendered the first time it is needed.
/// </summary>
/// <remarks>
/// The size of the first render is kept for good, so a stat and a read cannot disagree; the bytes
/// are held only weakly, so a page that was read once and walked away from is let go. This is
/// only safe because the text is fixed — nothing in it comes from a clock or from a command — so
/// a second render is byte-for-byte the first. Anything that moves is a
/// <see cref="LivePage"/> instead.
/// </remarks>
internal sealed class TextPage(string name, TerminalNodeKind kind, string key, Func<string> render)
    : TerminalPage(name, kind, key)
{
    private readonly Lock gate = new();
    private WeakReference<byte[]>? content;
    private int size = -1;

    /// <inheritdoc />
    public override ValueTask<ReadOnlyMemory<byte>> ContentAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (content is not null && content.TryGetTarget(out byte[]? held))
            {
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(held);
            }

            byte[] rendered = Encoding.UTF8.GetBytes(render());

            content = new WeakReference<byte[]>(rendered);
            size = rendered.Length;

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(rendered);
        }
    }

    /// <inheritdoc />
    public override async ValueTask<ulong> SizeAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (size >= 0)
            {
                return (ulong)size;
            }
        }

        return (ulong)(await ContentAsync(cancellationToken).ConfigureAwait(false)).Length;
    }
}
