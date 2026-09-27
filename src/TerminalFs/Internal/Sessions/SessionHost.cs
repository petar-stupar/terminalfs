using System.Diagnostics;
using TerminalFs.Internal.Mount;

namespace TerminalFs.Internal.Sessions;

/// <summary>The real thing: this binary as the server, and the machine's own mount table.</summary>
internal sealed class SessionHost : ISessionHost
{
    /// <summary>
    /// Runs the command it is given with no terminal and its output appended to a log, in a
    /// session of its own when <c>setsid</c> is there to make one.
    /// </summary>
    /// <remarks>
    /// The redirection is done by a shell rather than by this process because the server has to
    /// outlive it: a pipe back to here would break the moment <c>start</c> returned. The shell
    /// also closes the caller's standard output before the server runs, which matters when the
    /// caller is an agent's hook — a harness waits for that pipe to close, and a server holding
    /// it would hang the hook for the life of the session.
    /// </remarks>
    private const string Detach = """
        log=$1; shift
        if command -v setsid >/dev/null 2>&1; then set -- setsid "$@"; fi
        exec "$@" </dev/null >>"$log" 2>&1
        """;

    /// <inheritdoc />
    public Process Launch(string id, string workingDirectory, string log)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };

        foreach (string argument in (string[])["-c", Detach, "terminalfs-session", log, .. Self()])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string argument in (string[])["session", "serve", "--id", id, "--cwd", workingDirectory])
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new MountException($"could not start the server for session {id}");
    }

    /// <inheritdoc />
    public async Task<bool> IsMountedAsync(SessionRecord record, CancellationToken cancellationToken) =>
        await HostMount.IdentifyAsync(Settings(record), cancellationToken).ConfigureAwait(false) is not null;

    /// <inheritdoc />
    public Task UnmountAsync(SessionRecord record, CancellationToken cancellationToken) =>
        HostMount.UnmountAsync(Settings(record), cancellationToken);

    private static MountSettings Settings(SessionRecord record) =>
        new(record.MountPath, MountStrategy.Native, record.Port, SmbPort: 0);

    /// <summary>
    /// How to run this program again. A published build is its own executable; a build run
    /// through <c>dotnet</c> is the host plus the assembly it was handed.
    /// </summary>
    private static string[] Self()
    {
        string process = Environment.ProcessPath
            ?? throw new MountException("cannot tell which executable this is, so cannot start a session server");

        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            // Not Assembly.Location, which is empty in a single-file build.
            ? [process, Path.Combine(AppContext.BaseDirectory, "terminalfs.dll")]
            : [process];
    }
}
