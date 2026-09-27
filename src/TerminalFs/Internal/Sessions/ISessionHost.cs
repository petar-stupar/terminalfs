namespace TerminalFs.Internal.Sessions;

/// <summary>
/// The parts of a session that reach outside this process: launching its server, and asking about
/// or undoing its mount. Separate so the lifecycle can be tested on a machine that cannot mount.
/// </summary>
internal interface ISessionHost
{
    /// <summary>
    /// Starts the server for session <paramref name="id"/> in a session of its own, writing what
    /// it prints to <paramref name="log"/>. It writes its record once its tree is mounted.
    /// </summary>
    /// <returns>The server's process, for noticing early that it died.</returns>
    System.Diagnostics.Process Launch(string id, string workingDirectory, string log);

    /// <summary>
    /// Reads the mount table once, and answers from that reading: the port of the loopback 9P
    /// mount at exactly a given path, or null when nothing of that kind is mounted there.
    /// </summary>
    /// <exception cref="Mount.MountException">
    /// The table could not be read. Never read as "nothing is mounted": that answer is what
    /// makes it safe to look inside a session's directory.
    /// </exception>
    Task<Func<string, int?>> ReadMountsAsync(CancellationToken cancellationToken);

    /// <summary>Detaches the mount at <paramref name="mountPath"/>, which serves <paramref name="port"/>.</summary>
    Task UnmountAsync(string mountPath, int port, CancellationToken cancellationToken);
}
