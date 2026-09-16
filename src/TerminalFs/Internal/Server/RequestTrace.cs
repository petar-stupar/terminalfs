using System.Collections.Concurrent;
using NineP.Protocol;
using NineP.Server;

namespace TerminalFs.Internal.Server;

/// <summary>
/// Reports the first time each kind of request arrives, and the first time each kind is refused.
/// </summary>
/// <remarks>
/// This exists to answer one question that guessing cannot: which messages does a real kernel
/// client actually send? A userspace test client sends what its author thought of. A v9fs mount
/// running a shell sends what the kernel needs, and a handler that is missing shows up here as a
/// refusal rather than as a tool behaving strangely three layers up — which is how the absence of
/// <c>Tstatfs</c> presented itself the first time.
/// </remarks>
internal sealed class RequestTrace : IRequestLogSink
{
    private readonly ConcurrentDictionary<MessageType, bool> _seen = new();
    private readonly ConcurrentDictionary<(MessageType Request, int Errno), int> _refusals = new();

    /// <inheritdoc />
    public void Record(in RequestLogEntry entry)
    {
        if (_seen.TryAdd(entry.Request, true))
        {
            Console.Error.WriteLine($"[9p] first {entry.Request} identity={entry.Identity?.User ?? "-"}/{entry.Identity?.Uid}");
        }

        if (entry.Error is not { } error)
        {
            return;
        }

        // Every distinct refusal is printed once with its first summary. Printing them all would
        // bury the one that matters under a directory walk's worth of ENOENT.
        int count = _refusals.AddOrUpdate((entry.Request, error.Errno), 1, (_, previous) => previous + 1);

        if (count == 1)
        {
            Console.Error.WriteLine(
                $"[9p] REFUSED {entry.Request} -> {error.Ename} (errno {error.Errno}) {entry.Summary}");
        }
    }

    /// <summary>What was seen, for a report at the end of a run.</summary>
    internal string Report()
    {
        IEnumerable<string> requests = _seen.Keys
            .OrderBy(type => type.ToString(), StringComparer.Ordinal)
            .Select(type => type.ToString());

        IEnumerable<string> refusals = _refusals
            .OrderByDescending(pair => pair.Value)
            .Select(pair => $"{pair.Key.Request} errno {pair.Key.Errno} x{pair.Value}");

        return $"messages seen: {string.Join(", ", requests)}\nrefusals: {string.Join("; ", refusals)}";
    }
}
