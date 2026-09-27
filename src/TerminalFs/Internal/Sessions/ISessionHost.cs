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
    /// The port of the loopback 9P mount at exactly <paramref name="mountPath"/>, or null when
    /// nothing of that kind is mounted there.
    /// </summary>
    Task<int?> MountedPortAsync(string mountPath, CancellationToken cancellationToken);

    /// <summary>Detaches the mount at <paramref name="mountPath"/>, which serves <paramref name="port"/>.</summary>
    Task UnmountAsync(string mountPath, int port, CancellationToken cancellationToken);
}
