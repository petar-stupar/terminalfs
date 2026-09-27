namespace TerminalFs.Internal.Sessions;

/// <summary>
/// The parts of a session that reach outside this process: launching its server, and asking about
/// or undoing its mount. Separate so the lifecycle can be tested on a machine that cannot mount.
/// </summary>
internal interface ISessionHost
{
    /// <summary>
    /// Starts the server for session <paramref name="id"/> in the background, writing what it
    /// prints to <paramref name="log"/>. It writes its record once its tree is mounted.
    /// </summary>
    /// <returns>The process launched, for noticing early that it died.</returns>
    System.Diagnostics.Process Launch(string id, string workingDirectory, string log);

    /// <summary>Whether the tree <paramref name="record"/> describes is still mounted.</summary>
    Task<bool> IsMountedAsync(SessionRecord record, CancellationToken cancellationToken);

    /// <summary>Detaches the tree <paramref name="record"/> describes.</summary>
    Task UnmountAsync(SessionRecord record, CancellationToken cancellationToken);
}
