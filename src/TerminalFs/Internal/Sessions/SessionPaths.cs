namespace TerminalFs.Internal.Sessions;

/// <summary>
/// Where each session's tree, record, log and lock live. A session is named by the agent that
/// owns it, so everything about it is found from that name and nothing else.
/// </summary>
/// <param name="Root">The directory every session lives under.</param>
internal sealed record SessionPaths(string Root)
{
    /// <summary>The longest session id accepted.</summary>
    internal const int MaxIdLength = 128;

    /// <summary>
    /// The runtime directory of this user, as the machine they are on names it.
    /// </summary>
    /// <remarks>
    /// <c>$XDG_RUNTIME_DIR</c> is the right place, because it is per user, private, and emptied
    /// at logout — which is exactly the lifetime of a mount made for an agent session. macOS has
    /// no such directory and neither does a container that was not started by a login, so the
    /// fallback is the cache directory: private to the user and never mistaken for data.
    /// </remarks>
    internal static SessionPaths Default { get; } = Resolve(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The same answer, computed from an environment and a home directory in hand.</summary>
    internal static SessionPaths Resolve(Func<string, string?> environment, string home)
    {
        string? runtime = environment("XDG_RUNTIME_DIR");

        if (!string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime))
        {
            return new SessionPaths(Path.Combine(runtime, "terminalfs"));
        }

        string? cache = environment("XDG_CACHE_HOME");

        return new SessionPaths(Path.Combine(
            !string.IsNullOrEmpty(cache) && Path.IsPathRooted(cache) ? cache : Path.Combine(home, ".cache"),
            "terminalfs"));
    }

    /// <summary>Where the session's tree is mounted.</summary>
    internal string MountPath(string id) => Path.Combine(Root, Checked(id));

    /// <summary>What the session's server wrote about itself once it was mounted.</summary>
    internal string RecordPath(string id) => Path.Combine(Root, Checked(id) + ".session");

    /// <summary>Everything the session's server printed.</summary>
    internal string LogPath(string id) => Path.Combine(Root, Checked(id) + ".log");

    /// <summary>Held by whoever is starting or stopping the session.</summary>
    internal string LockPath(string id) => Path.Combine(Root, Checked(id) + ".lock");

    /// <summary>The ids of every session with a record.</summary>
    internal IEnumerable<string> RecordedIds() =>
        Directory.Exists(Root)
            ? Directory.EnumerateFiles(Root, "*.session")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(IsValidId)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>
    /// Whether <paramref name="id"/> can name a session. It becomes a directory name, so it is
    /// held to the characters a command name is: nothing that could climb out of the root, hide
    /// itself with a leading dot, or need quoting in the mount table.
    /// </summary>
    internal static bool IsValidId(string id) =>
        id.Length is > 0 and <= MaxIdLength
        && id[0] != '.'
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    private static string Checked(string id) =>
        IsValidId(id) ? id : throw new ArgumentException($"'{id}' is not a session id", nameof(id));
}
