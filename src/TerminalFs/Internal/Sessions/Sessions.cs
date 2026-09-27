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
/// on disk always means "this was mounted, at this port, by this process". A stop issued days
/// later by a different process finds the server from that record alone, and finds the mount
/// from the mount table at the session's own path, so a mount whose record was never written is
/// still cleared up.
/// </para>
/// </remarks>
internal sealed class Sessions(SessionPaths paths, ISessionHost host, Action<string> report)
{
    /// <summary>How long a start waits for the server to mount its tree.</summary>
    /// <remarks>Inside the minute an agent harness usually gives a hook.</remarks>
    internal TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>How long a stop waits for the server to shut down cleanly before killing it.</summary>
    internal TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for somebody else starting, stopping or collecting sessions.</summary>
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Makes sure session <paramref name="id"/> has a mounted tree, and returns where it is.
    /// </summary>
    /// <remarks>
    /// Idempotent: a session whose server is running and whose tree is mounted is left alone and
    /// its path returned. One that is half there — a server that died, a mount that went away, a
    /// mount nobody recorded — is cleared up and started again, because an agent handed a path
    /// that does not work is worse off than one that waited for a new one.
    /// </remarks>
    internal async Task<string> StartAsync(string id, string workingDirectory, CancellationToken cancellationToken)
    {
        using FileStream held = await LockAsync(cancellationToken).ConfigureAwait(false);

        SessionRecord? existing = Recorded(id);

        if (existing is not null
            && existing.ServerIsAlive()
            && await host.MountedPortAsync(existing.MountPath, cancellationToken).ConfigureAwait(false) == existing.Port)
        {
            report($"session {id} is already mounted at {existing.MountPath}");

            // Changing it would mean restarting the server, and so killing whatever it is
            // running; an agent resuming from another directory is told instead.
            if (existing.WorkingDirectory != workingDirectory)
            {
                report($"its commands run in {existing.WorkingDirectory}, not {workingDirectory}; "
                    + "stop the session to change that");
            }

            return existing.MountPath;
        }

        await TearDownAsync(id, existing, cancellationToken).ConfigureAwait(false);

        string mountPath = paths.MountPath(id);
        SessionFiles.CheckMountPoint(mountPath);

        string log = paths.LogPath(id);
        SessionFiles.CreatePrivate(log).Dispose();

        using Process server = host.Launch(id, workingDirectory, log);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;

        while (true)
        {
            if (Recorded(id) is { } record)
            {
                return record.MountPath;
            }

            if (server.HasExited || DateTimeOffset.UtcNow > deadline)
            {
                string reason = server.HasExited
                    ? $"its server exited with {server.ExitCode.ToString(CultureInfo.InvariantCulture)}"
                    : $"its tree was not mounted within {StartTimeout.TotalSeconds:0} seconds";

                if (!server.HasExited)
                {
                    server.Kill(entireProcessTree: true);
                    await server.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                string why = Tail(log);

                // The mount may have finished just as the deadline passed, with no record to say so.
                await TearDownAsync(id, null, CancellationToken.None).ConfigureAwait(false);

                throw new MountException($"session {id} did not start: {reason}.{why}");
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
        using FileStream held = await LockAsync(cancellationToken).ConfigureAwait(false);

        if (!Leftovers(id).Any())
        {
            report($"no session {id}; nothing to stop");

            return false;
        }

        await TearDownAsync(id, Recorded(id), cancellationToken).ConfigureAwait(false);
        report($"stopped session {id}");

        return true;
    }

    /// <summary>
    /// Stops every session whose server has gone, and, when <paramref name="olderThan"/> is given,
    /// every session started longer ago than that; then removes whatever no session claims.
    /// </summary>
    /// <remarks>
    /// Only a dead server is collected unless an age is given, because age is a guess: nothing
    /// here can see the agent a session was started for, and a session older than the limit may
    /// be running commands somebody is still waiting on.
    /// </remarks>
    /// <returns>How many sessions were stopped.</returns>
    internal async Task<int> CollectAsync(TimeSpan? olderThan, CancellationToken cancellationToken)
    {
        using FileStream held = await LockAsync(cancellationToken).ConfigureAwait(false);

        int stopped = 0;

        foreach (string id in paths.RecordedIds())
        {
            if (Recorded(id) is not { } record)
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
            report($"stopped session {id}: {why}");
            stopped++;
        }

        // Whatever is left has no record: a start that never finished, a record discarded above,
        // or a directory left by a mount that is gone.
        foreach (string id in Unclaimed())
        {
            await TearDownAsync(id, null, cancellationToken).ConfigureAwait(false);
        }

        return stopped;
    }

    /// <summary>
    /// Everything a session leaves: its server and the commands in its process group, its mount,
    /// its directory, its record and its log.
    /// </summary>
    private async Task TearDownAsync(string id, SessionRecord? record, CancellationToken cancellationToken)
    {
        if (record is not null)
        {
            if (record.ServerIsAlive())
            {
                await TerminateAsync(record, cancellationToken).ConfigureAwait(false);
            }

            if (record.ServerIsGone())
            {
                await KillGroupAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }

        // The server unmounts on its way out, so this is for one that could not: killed, crashed,
        // or never recorded.
        string mountPath = paths.MountPath(id);

        if (await host.MountedPortAsync(mountPath, cancellationToken).ConfigureAwait(false) is int port)
        {
            await host.UnmountAsync(mountPath, port, cancellationToken).ConfigureAwait(false);
        }

        RemoveEmptyDirectory(mountPath);

        foreach (string file in (string[])[paths.RecordPath(id), paths.RecordPath(id) + ".tmp", paths.LogPath(id)])
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Asks the server to stop, which unmounts its tree and kills its commands, and kills it and
    /// everything under it if it has not gone in time.
    /// </summary>
    private async Task TerminateAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        await Signal("-TERM", record.Pid, cancellationToken).ConfigureAwait(false);

        if (await ExitedAsync(record, StopTimeout, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        report($"session {record.Id}'s server did not stop within {StopTimeout.TotalSeconds:0} seconds; killing it");

        try
        {
            using Process process = Process.GetProcessById(record.Pid);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Gone between the check and the kill, which is what the kill wanted.
        }

        await ExitedAsync(record, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ExitedAsync(SessionRecord record, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (record.ServerIsAlive())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Kills whatever is left of a dead server's process group: the commands of a server that was
    /// killed or crashed, which nothing else will ever stop.
    /// </summary>
    /// <remarks>
    /// The server runs in a session of its own, so its pid is the process group of every command
    /// it starts. The group is only signalled once the record says the server is gone — no
    /// process has its pid, or the one that does is its own zombie — and Linux does not hand out a
    /// pid that is still in use as a group id. So a group by that number is this server's
    /// leftovers and nobody else's.
    /// </remarks>
    private static Task<CommandResult> KillGroupAsync(SessionRecord record, CancellationToken cancellationToken) =>
        Signal("-KILL", -record.Pid, cancellationToken);

    private static Task<CommandResult> Signal(string signal, int target, CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(
            "kill",
            [signal, "--", target.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);

    /// <summary>
    /// The record of session <paramref name="id"/>, or null. A record that does not describe this
    /// session is reported and discarded, so that whatever it left is cleared up as unrecorded
    /// rather than stopping every later start, stop and collect in the same place.
    /// </summary>
    private SessionRecord? Recorded(string id)
    {
        string path = paths.RecordPath(id);

        if (!File.Exists(path))
        {
            return null;
        }

        if (SessionRecord.Read(path) is { } record && record.Describes(id, paths.MountPath(id)))
        {
            return record;
        }

        report($"discarding {path}: it does not describe session {id}");
        File.Delete(path);

        return null;
    }

    /// <summary>What session <paramref name="id"/> has on disk.</summary>
    private IEnumerable<string> Leftovers(string id) =>
        ((string[])[paths.MountPath(id), paths.RecordPath(id), paths.RecordPath(id) + ".tmp", paths.LogPath(id)])
            .Where(path => Directory.Exists(path) || File.Exists(path));

    /// <summary>Sessions with something on disk and no record.</summary>
    private List<string> Unclaimed()
    {
        if (!Directory.Exists(paths.Root))
        {
            return [];
        }

        var ids = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string entry in Directory.EnumerateFileSystemEntries(paths.Root))
        {
            string name = Path.GetFileName(entry);

            string? id = Directory.Exists(entry) ? name
                : name.EndsWith(".log", StringComparison.Ordinal) ? name[..^".log".Length]
                : name.EndsWith(".session.tmp", StringComparison.Ordinal) ? name[..^".session.tmp".Length]
                : null;

            if (id is not null && SessionPaths.IsValidId(id) && !File.Exists(paths.RecordPath(id)))
            {
                ids.Add(id);
            }
        }

        return [.. ids];
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
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report($"left {path} in place: {exception.Message}");
        }
    }

    /// <summary>
    /// The lock every start, stop and collect holds for as long as it runs.
    /// </summary>
    /// <remarks>
    /// One lock for the whole root rather than one per session, and never deleted. A lock file that
    /// is deleted while held lets the next caller create a fresh one and hold that at the same
    /// time as somebody still waiting on the old; a lock per session also leaves a collect racing
    /// a start for the directory it is about to sweep. Starts are serialised by this, which costs
    /// a fraction of a second each when mounting works.
    /// </remarks>
    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        SessionFiles.SecureRoot(paths.Root);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + LockTimeout;

        while (true)
        {
            try
            {
                // On Unix an unshared FileStream is an advisory flock, so it is released however
                // its holder ends — including by being killed.
                return new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (IsHeld(exception))
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new MountException(
                        $"another terminalfs session command has held {paths.LockPath} for "
                        + $"{LockTimeout.TotalSeconds:0} seconds");
                }
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the open failed because somebody holds the lock, rather than for a reason that
    /// waiting will not fix. .NET reports a held flock with the errno as the HResult.
    /// </summary>
    private static bool IsHeld(IOException exception) => exception.HResult == (
        OperatingSystem.IsWindows() ? unchecked((int)0x80070020)
        : OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 35
        : 11);

    /// <summary>
    /// The end of a session's log, for saying why it did not start. This program's own messages
    /// come first: the one that matters is usually the first line, not the last.
    /// </summary>
    private static string Tail(string log)
    {
        try
        {
            string[] lines = File.ReadAllLines(log);
            string[] ours = [.. lines.Where(line => line.StartsWith("terminalfs:", StringComparison.Ordinal)).Take(10)];
            string[] shown = ours.Length > 0 ? ours : [.. lines.TakeLast(10)];

            return shown.Length == 0 ? string.Empty : "\n  " + string.Join("\n  ", shown);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
