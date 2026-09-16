using System.Collections.Concurrent;

namespace TerminalFs.Internal.Server;

/// <summary>
/// Hands each node of the tree a qid path and keeps handing it the same one.
/// </summary>
/// <remarks>
/// A qid is how a 9P client tells two files apart and how it decides whether what it cached is
/// still the file it walked to. The tree builds its nodes lazily, so the same page can be a
/// different object on a second walk; the node's key does not change, which is what this maps.
/// Numbers are handed out in order rather than hashed, because a hash of a path collides and two
/// files sharing a qid is a bug a client will act on.
/// </remarks>
internal sealed class QidPaths
{
    private readonly ConcurrentDictionary<string, ulong> _paths = new(StringComparer.Ordinal);
    private ulong _next;

    /// <summary>The qid path for <paramref name="key"/>, allocated on first use.</summary>
    internal ulong Of(string key) =>
        _paths.GetOrAdd(key, _ => Interlocked.Increment(ref _next));
}
