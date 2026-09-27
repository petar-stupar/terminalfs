using System.Diagnostics;
using TerminalFs.Internal.Mount;

namespace TerminalFs.Internal.Sessions;

/// <summary>The real thing: this binary as the server, and the machine's own mount table.</summary>
internal sealed class SessionHost : ISessionHost
{
    /// <summary>
    /// Runs the command it is given in a session of its own, with no terminal and its output
    /// appended to a log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The redirection is done by a shell rather than by this process because the server has to
    /// outlive it: a pipe back to here would break the moment <c>start</c> returned. The shell
    /// also closes the caller's standard output before the server runs, which matters when the
    /// caller is an agent's hook — a harness waits for that pipe to close, and a server holding
    /// it would hang the hook for the life of the session.
    /// </para>
    /// <para>
    /// <c>setsid</c> is required, not a nicety. Its own session puts the server out of reach of a
    /// harness that kills the hook's process group, and makes the server's pid the group of
    /// every command it starts — which is how a stop finds the commands of a server that was
    /// killed. The shell is not a group leader, so <c>setsid</c> does not fork and the pid this
    /// returns is the server's.
    /// </para>
    /// </remarks>
    private const string Detach = """
        log=$1; shift
        exec setsid "$@" </dev/null >>"$log" 2>&1
        """;

    /// <inheritdoc />
    public Process Launch(string id, string workingDirectory, string log)
    {
        if (!ProcessRunner.Exists("setsid"))
        {
            throw new MountException("sessions need setsid, which is part of util-linux; install it and try again");
        }

        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };

        foreach (string argument in (string[])
            ["-c", Detach, "terminalfs-session", log, .. Self(), "session", "serve", "--id", id, "--cwd", workingDirectory])
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start)
            ?? throw new MountException($"could not start the server for session {id}");
    }

    /// <inheritdoc />
    public Task<int?> MountedPortAsync(string mountPath, CancellationToken cancellationToken) =>
        HostMount.NinePPortAtAsync(mountPath, cancellationToken);

    /// <inheritdoc />
    public Task UnmountAsync(string mountPath, int port, CancellationToken cancellationToken) =>
        HostMount.UnmountIdentifiedAsync(mountPath, cancellationToken);

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
