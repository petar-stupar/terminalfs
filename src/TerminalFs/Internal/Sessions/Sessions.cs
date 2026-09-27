using System.Diagnostics;
using System.Globalization;
using TerminalFs.Internal.Mount;

namespace TerminalFs.Internal.Sessions;

/// <summary>
/// Starts, stops and collects per-session trees: one server, on its own port, mounted at its own
/// directory, for each agent session that asks for one.
/// </summary>
/// <remarks>
/// <para>
/// One shared server cannot tell which agent wrote to <c>/ctl</c>. A tree per session makes that
/// a question about the path, which anything watching the write can answer.
/// </para>
/// <para>
/// The session's server writes its own record, and only once its tree is mounted, so a record
/// on disk always means "this was mounted, at this port, by this process". Everything else here
/// reads that record and nothing else: a stop issued days later by a different process finds the
/// server and the mount from it alone.
/// </para>
/// </remarks>
internal sealed class Sessions(SessionPaths paths, ISessionHost host, Action<string> report)
{
    /// <summary>How long a start waits for the server to mount its tree.</summary>
    /// <remarks>A mount can take up to a minute before it fails on its own.</remarks>
    internal TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How long a stop waits for the server to shut down cleanly before killing it.</summary>
    internal TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait for somebody else starting or stopping the same session.</summary>
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Makes sure session <paramref name="id"/> has a mounted tree, and returns where it is.
    /// </summary>
    /// <remarks>
    /// Idempotent: a session whose server is running and whose tree is mounted is left alone and
    /// its path returned. One that is half there — a server that died, a mount that went away — is
    /// cleared up and started again, because an agent handed a path that does not work is worse
    /// off than one that waited for a new one.
    /// </remarks>
    internal async Task<string> StartAsync(string id, string workingDirectory, CancellationToken cancellationToken)
    {
        using FileStream held = await LockAsync(id, cancellationToken).ConfigureAwait(false);

        if (SessionRecord.Read(paths.RecordPath(id)) is { } existing)
        {
            if (existing.ServerIsAlive() && await host.IsMountedAsync(existing, cancellationToken).ConfigureAwait(false))
            {
                report($"session {id} is already mounted at {existing.MountPath}");

                return existing.MountPath;
            }

            report($"session {id} was left half-stopped; clearing it up first");
            await TearDownAsync(id, existing, cancellationToken).ConfigureAwait(false);
        }

        string log = paths.LogPath(id);
        File.Delete(log);

        using Process launched = host.Launch(id, workingDirectory, log);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;

        while (true)
        {
            if (SessionRecord.Read(paths.RecordPath(id)) is { } record)
            {
                return record.MountPath;
            }

            // Exiting is not failing on its own: setsid forks when it has to, and the process
            // launched then leaves with 0 while the server carries on. Only a failure is final.
            bool failed = launched.HasExited && launched.ExitCode != 0;

            if (failed || DateTimeOffset.UtcNow > deadline)
            {
                if (!launched.HasExited)
                {
                    launched.Kill(entireProcessTree: true);
                }

                string reason = failed
                    ? $"its server exited with {launched.ExitCode.ToString(CultureInfo.InvariantCulture)}"
                    : $"its tree was not mounted within {StartTimeout.TotalSeconds:0} seconds";

                RemoveEmptyDirectory(paths.MountPath(id));

                throw new MountException($"session {id} did not start: {reason}.{Tail(log)}");
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops session <paramref name="id"/>'s server, which kills its commands, and removes its
    /// mount and directory. Safe to call when nothing is there.
    /// </summary>
    /// <returns>Whether there was anything to stop.</returns>
    internal async Task<bool> StopAsync(string id, CancellationToken cancellationToken)
    {
        using FileStream held = await LockAsync(id, cancellationToken).ConfigureAwait(false);

        SessionRecord? record = SessionRecord.Read(paths.RecordPath(id));

        if (record is null && !Directory.Exists(paths.MountPath(id)))
        {
            report($"no session {id}; nothing to stop");
            File.Delete(paths.LockPath(id));

            return false;
        }

        await TearDownAsync(id, record, cancellationToken).ConfigureAwait(false);
        File.Delete(paths.LockPath(id));
        report($"stopped session {id}");

        return true;
    }

    /// <summary>
    /// Stops every session whose server has gone, or that was started longer ago than
    /// <paramref name="olderThan"/>, and removes what sessions left behind.
    /// </summary>
    /// <remarks>
    /// Age is the test for an agent that went away without stopping its session, because nothing
    /// else here can see the agent: the server was started by a hook that has long since exited.
    /// A session somebody is starting or stopping right now is skipped rather than waited for.
    /// </remarks>
    /// <returns>How many sessions were stopped.</returns>
    internal async Task<int> CollectAsync(TimeSpan olderThan, CancellationToken cancellationToken)
    {
        int stopped = 0;

        foreach (string id in paths.RecordedIds())
        {
            using FileStream? held = TryLock(id);

            if (held is null || SessionRecord.Read(paths.RecordPath(id)) is not { } record)
            {
                continue;
            }

            string? why = !record.ServerIsAlive() ? "its server is gone"
                : DateTimeOffset.UtcNow - record.Created > olderThan ? $"it was started at {record.Created:u}"
                : null;

            if (why is null)
            {
                continue;
            }

            await TearDownAsync(id, record, cancellationToken).ConfigureAwait(false);
            File.Delete(paths.LockPath(id));
            report($"stopped session {id}: {why}");
            stopped++;
        }

        foreach (string id in Unrecorded())
        {
            using FileStream? held = TryLock(id);

            if (held is not null && !File.Exists(paths.RecordPath(id)))
            {
                RemoveEmptyDirectory(paths.MountPath(id));
                File.Delete(paths.LogPath(id));
                File.Delete(paths.LockPath(id));
            }
        }

        return stopped;
    }

    /// <summary>
    /// Everything a session leaves: its server, its mount, its directory, its record and its log.
    /// </summary>
    private async Task TearDownAsync(string id, SessionRecord? record, CancellationToken cancellationToken)
    {
        if (record is not null)
        {
            if (record.ServerIsAlive())
            {
                await TerminateAsync(record, cancellationToken).ConfigureAwait(false);
            }

            await KillOrphanedCommandsAsync(record, cancellationToken).ConfigureAwait(false);

            // The server unmounts on its way out, so this is for one that could not: killed, or
            // crashed. The record's port is what identifies the mount as ours.
            if (await host.IsMountedAsync(record, cancellationToken).ConfigureAwait(false))
            {
                await host.UnmountAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        RemoveEmptyDirectory(paths.MountPath(id));
        File.Delete(paths.RecordPath(id));
        File.Delete(paths.LogPath(id));
    }

    /// <summary>
    /// Asks the server to stop, which unmounts its tree and kills its commands, and kills it and
    /// everything under it if it has not gone in time.
    /// </summary>
    private async Task TerminateAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        await ProcessRunner.RunAsync(
            "kill",
            ["-TERM", record.Pid.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + StopTimeout;

        while (record.ServerIsAlive())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                report($"session {record.Id}'s server did not stop within {StopTimeout.TotalSeconds:0} seconds; killing it");
                Kill(record.Pid);

                break;
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Kills whatever is left of the server's process group: the commands of a server that was
    /// killed or crashed, which nothing else will ever stop.
    /// </summary>
    /// <remarks>
    /// The server is launched in a session of its own, so its pid is its process group, and every
    /// command it starts is in that group. The group is only signalled when no process has the
    /// server's pid: Linux does not hand out a pid that is still in use as a group id, so a group
    /// by that number with no leader is this server's leftovers and nobody else's. A pid in use
    /// means the number has been given to something else, which is left alone.
    /// </remarks>
    private static async Task KillOrphanedCommandsAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        if (Exists(record.Pid))
        {
            return;
        }

        await ProcessRunner.RunAsync(
            "kill",
            ["-KILL", "--", "-" + record.Pid.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static bool Exists(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void Kill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Gone between the check and the kill, which is what the kill wanted.
        }
    }

    /// <summary>
    /// Removes a session's directory if it is empty. Never recursively: a directory with anything
    /// in it may still be a mount, and deleting through a mount of this tree removes files from
    /// wherever its commands wrote them.
    /// </summary>
    private void RemoveEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report($"left {path} in place: {exception.Message}");
        }
    }

    /// <summary>Session directories and logs under the root that have no record.</summary>
    private List<string> Unrecorded()
    {
        if (!Directory.Exists(paths.Root))
        {
            return [];
        }

        return Directory.EnumerateFileSystemEntries(paths.Root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Select(name => name.EndsWith(".log", StringComparison.Ordinal) ? name[..^".log".Length] : name)
            .Where(SessionPaths.IsValidId)
            .Where(id => Directory.Exists(paths.MountPath(id)) || File.Exists(paths.LogPath(id)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private async Task<FileStream> LockAsync(string id, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + LockTimeout;

        while (true)
        {
            if (TryLock(id) is { } held)
            {
                return held;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new MountException($"session {id} is being started or stopped by something else");
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The session's lock, or null when somebody else holds it. On Unix an unshared
    /// <see cref="FileStream"/> is an advisory <c>flock</c>, so it is released however its holder
    /// ends — including by being killed, which a lock file that merely exists is not.
    /// </summary>
    private FileStream? TryLock(string id)
    {
        CreateRoot();

        try
        {
            return new FileStream(paths.LockPath(id), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The root is private to the user: the records in it say how to reach servers that run
    /// commands as them.
    /// </summary>
    private void CreateRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(paths.Root);

            return;
        }

        Directory.CreateDirectory(
            paths.Root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>The end of a session's log, for saying why it did not start.</summary>
    private static string Tail(string log)
    {
        try
        {
            string[] lines = File.ReadAllLines(log);

            return lines.Length == 0 ? string.Empty : "\n  " + string.Join("\n  ", lines.TakeLast(20));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
