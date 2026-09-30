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
    /// <para>
    /// <c>$XDG_RUNTIME_DIR</c> is the right place, because it is per user, private, and emptied
    /// at logout — which is exactly the lifetime of a mount made for an agent session. macOS has
    /// no such directory and neither does a container that was not started by a login, so the
    /// fallback is the cache directory: private to the user and never mistaken for data.
    /// </para>
    /// <para>
    /// <c>$TERMINALFS_RUNTIME_DIR</c> comes before both, for a machine that wants its session trees
    /// somewhere it chose — a container that bind-mounts one directory for every agent in it, say
    /// — without moving everything else that reads <c>$XDG_RUNTIME_DIR</c>.
    /// </para>
    /// </remarks>
    internal static SessionPaths Default { get; } = Resolve(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The same answer, computed from an environment and a home directory in hand.</summary>
    internal static SessionPaths Resolve(Func<string, string?> environment, string home)
    {
        foreach (string variable in (string[])["TERMINALFS_RUNTIME_DIR", "XDG_RUNTIME_DIR"])
        {
            string? runtime = environment(variable);

            if (!string.IsNullOrEmpty(runtime) && Path.IsPathRooted(runtime))
            {
                return new SessionPaths(Path.Combine(runtime, "terminalfs"));
            }
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

    /// <summary>
    /// Held by whoever is starting, stopping or collecting sessions. A session id cannot start
    /// with a dot, so this can never be mistaken for one.
    /// </summary>
    internal string LockPath => Path.Combine(Root, ".lock");

    /// <summary>
    /// Whether <paramref name="id"/> can name a session. It becomes a directory name, so it is
    /// held to the characters a command name is: nothing that could climb out of the root, hide
    /// itself with a leading dot, or need quoting in the mount table. Nor may it end the way a
    /// session's own files do, or <c>abc.session</c> would be one session's directory and
    /// another's record.
    /// </summary>
    internal static bool IsValidId(string id) =>
        id.Length is > 0 and <= MaxIdLength
        && id[0] != '.'
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
        && !ReservedEndings.Any(ending => id.EndsWith(ending, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ReservedEndings = [".session", ".log", ".tmp"];

    private static string Checked(string id) =>
        IsValidId(id) ? id : throw new ArgumentException($"'{id}' is not a session id", nameof(id));
}
