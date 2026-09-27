using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using TerminalFs.Internal.Mount;

namespace TerminalFs.Internal.Sessions;

/// <summary>What a collection did.</summary>
/// <param name="Stopped">Sessions stopped.</param>
/// <param name="Failed">Sessions, or leftovers, that could not be cleared up.</param>
internal readonly record struct Collected(int Stopped, int Failed);

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
/// <para>
/// Nothing here looks inside a session's directory until the mount table says nothing is mounted
/// on it; see <see cref="SessionFiles.List"/> for why.
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
            if (Path.TrimEndingDirectorySeparator(existing.WorkingDirectory) != Path.TrimEndingDirectorySeparator(workingDirectory))
            {
                report($"its commands run in {existing.WorkingDirectory}, not {workingDirectory}; "
                    + "stop the session to change that");
            }

            return existing.MountPath;
        }

        string mountPath = paths.MountPath(id);

        // Before clearing anything up, which would quietly remove it: nothing this program makes
        // is a link, so one here was put there by somebody, and that is worth refusing over.
        // Stopping the session removes it.
        if (SessionFiles.List(paths.Root).GetValueOrDefault(id) == EntryKind.Link)
        {
            throw new MountException($"{mountPath} is a link; refusing to mount over it. 'terminalfs session stop --id {id}' removes it");
        }

        await TearDownAsync(id, existing, cancellationToken).ConfigureAwait(false);
        await CheckMountPointAsync(host, mountPath, cancellationToken).ConfigureAwait(false);

        string log = paths.LogPath(id);
        SessionFiles.CreatePrivate(log).Dispose();

        using Process server = host.Launch(id, workingDirectory, log);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StartTimeout;

        while (true)
        {
            string? failure = null;

            if (File.Exists(paths.RecordPath(id)))
            {
                if (SessionRecord.Read(paths.RecordPath(id)) is { } record && record.Describes(id, mountPath))
                {
                    return record.MountPath;
                }

                // Written whole by rename, so this is not a half-written one: the server and this
                // command disagree about what a record is, and waiting will not change that.
                failure = "its server wrote a record that does not describe it";
            }
            else if (server.HasExited)
            {
                failure = $"its server exited with {server.ExitCode.ToString(CultureInfo.InvariantCulture)}";
            }
            else if (DateTimeOffset.UtcNow > deadline)
            {
                failure = $"its tree was not mounted within {StartTimeout.TotalSeconds:0} seconds";
            }

            if (failure is not null)
            {
                if (!server.HasExited)
                {
                    KillTree(server, id);
                    await server.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }

                string why = Tail(log);

                // The mount may have finished just as the deadline passed, with no record to say so.
                await TearDownAsync(id, null, CancellationToken.None).ConfigureAwait(false);

                throw new MountException($"session {id} did not start: {failure}.{why}");
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

        Dictionary<string, EntryKind> listed = SessionFiles.List(paths.Root);
        bool present = ((string[])[id, id + ".session", id + ".session.tmp", id + ".log"]).Any(listed.ContainsKey)
            || await host.MountedPortAsync(paths.MountPath(id), cancellationToken).ConfigureAwait(false) is not null;

        if (!present)
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
    /// <para>
    /// Only a dead server is collected unless an age is given, because age is a guess: nothing
    /// here can see the agent a session was started for, and a session older than the limit may
    /// be running commands somebody is still waiting on.
    /// </para>
    /// <para>
    /// A session that cannot be cleared up — a mount somebody is still inside — is reported and
    /// passed over, so it does not keep every other session from being collected.
    /// </para>
    /// </remarks>
    internal async Task<Collected> CollectAsync(TimeSpan? olderThan, CancellationToken cancellationToken)
    {
        using FileStream held = await LockAsync(cancellationToken).ConfigureAwait(false);

        int stopped = 0;
        int failed = 0;

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

            if (await TryTearDownAsync(id, record, cancellationToken).ConfigureAwait(false))
            {
                report($"stopped session {id}: {why}");
                stopped++;
            }
            else
            {
                failed++;
            }
        }

        // Whatever is left has no record: a start that never finished, a record discarded above,
        // a server run by hand, or a directory left by a mount that is gone.
        foreach (string id in Unclaimed())
        {
            if (!await TryTearDownAsync(id, null, cancellationToken).ConfigureAwait(false))
            {
                failed++;
            }
        }

        return new Collected(stopped, failed);
    }

    private async Task<bool> TryTearDownAsync(string id, SessionRecord? record, CancellationToken cancellationToken)
    {
        try
        {
            await TearDownAsync(id, record, cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (exception is MountException or IOException or UnauthorizedAccessException)
        {
            report($"could not clear up session {id}: {exception.Message}");

            return false;
        }
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
                await SignalAsync("-KILL", -record.Pid, cancellationToken).ConfigureAwait(false);
            }
        }

        string mountPath = paths.MountPath(id);

        if (SessionFiles.List(paths.Root).GetValueOrDefault(id) == EntryKind.Link)
        {
            // Never followed: a session's directory made a link to another's must not get the
            // other unmounted. The link itself is this session's to remove.
            report($"{mountPath} was a link, not a session's directory; removed the link");
            File.Delete(mountPath);
        }
        else
        {
            // The server unmounts on its way out, so this is for one that could not: killed,
            // crashed, or never recorded.
            if (await host.MountedPortAsync(mountPath, cancellationToken).ConfigureAwait(false) is int port)
            {
                await host.UnmountAsync(mountPath, port, cancellationToken).ConfigureAwait(false);
            }

            RemoveEmptyDirectory(mountPath);
        }

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
        await SignalAsync("-TERM", record.Pid, cancellationToken).ConfigureAwait(false);

        if (await ExitedAsync(record, StopTimeout, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        report($"session {record.Id}'s server did not stop within {StopTimeout.TotalSeconds:0} seconds; killing it");

        try
        {
            using Process process = Process.GetProcessById(record.Pid);

            // Asked again after the wait: the pid is only this server's while its start time says so.
            if (record.ServerIsAlive())
            {
                KillTree(process, record.Id);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Gone between the check and the kill, which is what the kill wanted.
        }

        await ExitedAsync(record, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Kills <paramref name="process"/> and what it started, as far as this user may. A command
    /// run through <c>sudo</c> belongs to root and cannot be signalled from here; that is
    /// reported rather than allowed to stop the rest of the clean-up, which is what reaches it
    /// once the server is gone — its process group.
    /// </summary>
    private void KillTree(Process process, string id)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is AggregateException or Win32Exception)
        {
            report($"session {id}: not everything its server started could be killed: {exception.Message}");
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
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
    /// Sends <paramref name="signal"/> to <paramref name="target"/>: a pid, or a process group
    /// when negative.
    /// </summary>
    /// <remarks>
    /// A group is only signalled once the record says its server is gone — no process has the
    /// pid, or the one that does is its own zombie. The server runs in a session of its own, so
    /// its pid is the group of every command it starts, and Linux does not hand out a pid that
    /// is still in use as a group id: a group by that number holds this server's leftovers and
    /// nobody else's. No such process is the ordinary answer when there are none.
    /// </remarks>
    private async Task SignalAsync(string signal, int target, CancellationToken cancellationToken)
    {
        CommandResult result = await ProcessRunner.RunAsync(
            "kill",
            [signal, "--", target.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Missing)
        {
            report("kill is not installed, so no signal could be sent");
        }
        else if (!result.Ok && !result.Reason.Contains("No such process", StringComparison.OrdinalIgnoreCase))
        {
            report($"kill {signal} {target.ToString(CultureInfo.InvariantCulture)}: {result.Reason}");
        }
    }

    /// <summary>
    /// Refuses to mount where something is already mounted, or where there is anything but an
    /// empty directory or nothing. The mount table is asked first, so the directory is only
    /// looked into once nothing can be behind it.
    /// </summary>
    internal static async Task CheckMountPointAsync(ISessionHost host, string mountPath, CancellationToken cancellationToken)
    {
        if (await host.MountedPortAsync(mountPath, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new MountException($"{mountPath} is still mounted; stop the session first");
        }

        SessionFiles.CheckMountPoint(mountPath);
    }

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

    /// <summary>Sessions with something in the root and no record, from its listing alone.</summary>
    private List<string> Unclaimed()
    {
        Dictionary<string, EntryKind> listed = SessionFiles.List(paths.Root);
        var ids = new SortedSet<string>(StringComparer.Ordinal);

        foreach ((string name, EntryKind kind) in listed)
        {
            string? id = kind != EntryKind.File ? name
                : name.EndsWith(".log", StringComparison.Ordinal) ? name[..^".log".Length]
                : name.EndsWith(".session.tmp", StringComparison.Ordinal) ? name[..^".session.tmp".Length]
                : null;

            if (id is not null && SessionPaths.IsValidId(id) && !listed.ContainsKey(id + ".session"))
            {
                ids.Add(id);
            }
        }

        return [.. ids];
    }

    /// <summary>
    /// Removes a session's directory if it is empty. Never recursively: a directory with anything
    /// in it may still be a mount, and deleting through a mount of this tree removes files from
    /// wherever its commands wrote them. Only called once the mount table says nothing is mounted
    /// there, and only on what the listing says is a directory.
    /// </summary>
    private void RemoveEmptyDirectory(string path)
    {
        if (SessionFiles.List(paths.Root).GetValueOrDefault(Path.GetFileName(path)) != EntryKind.Directory)
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: false);
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
                return SessionFiles.OpenLock(paths.LockPath);
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
