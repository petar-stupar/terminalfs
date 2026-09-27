using System.Diagnostics;
using TerminalFs.Internal.Mount;
using TerminalFs.Internal.Sessions;

namespace TerminalFs.Tests;

/// <summary>
/// Per-session trees: started once, stopped completely, and collected when nobody stopped them.
/// </summary>
/// <remarks>
/// A real process stands in for each session's server, because what is being pinned is how
/// processes are found, stopped and told apart. The mount table is the one thing faked: this
/// suite runs where nothing can be mounted, and reading the real one is pinned by
/// <see cref="MountGuardTests"/>.
/// </remarks>
public sealed class SessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "terminalfs-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "terminalfs-scratch-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost host = new();
    private readonly List<Process> started = [];
    private readonly List<string> reports = [];
    private readonly SessionPaths paths;
    private readonly Sessions sessions;

    public SessionTests()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sessions are started and stopped with POSIX signals");

        Directory.CreateDirectory(scratch);
        paths = new SessionPaths(root);
        sessions = new Sessions(paths, host, reports.Add)
        {
            StartTimeout = TimeSpan.FromSeconds(10),
            StopTimeout = TimeSpan.FromSeconds(1),
            LockTimeout = TimeSpan.FromSeconds(5),
        };
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StartingASessionReturnsTheTreeItsServerMounted()
    {
        host.OnLaunch = (id, _, _) => ServeInBackground(id);

        string path = await sessions.StartAsync("s1", root, Token);

        Assert.Equal(paths.MountPath("s1"), path);
        Assert.Equal(1, host.Launches);
    }

    [Fact]
    public async Task TwoSessionsGetSeparateTrees()
    {
        host.OnLaunch = (id, _, _) => ServeInBackground(id);

        string first = await sessions.StartAsync("one", root, Token);
        string second = await sessions.StartAsync("two", root, Token);

        Assert.NotEqual(first, second);
        Assert.NotEqual(Record("one").Pid, Record("two").Pid);
        Assert.NotEqual(Record("one").Port, Record("two").Port);
    }

    [Fact]
    public async Task StartingASessionThatIsAlreadyMountedHandsBackTheSameTree()
    {
        SessionRecord running = Running("again");

        string path = await sessions.StartAsync("again", root, Token);

        Assert.Equal(running.MountPath, path);
        Assert.Equal(0, host.Launches);
    }

    /// <summary>
    /// Moving a running session would mean restarting its server and killing its commands, so it
    /// is not done — but a caller who asked for another directory is told they did not get it.
    /// </summary>
    [Fact]
    public async Task StartingAgainFromAnotherDirectorySaysWhereCommandsStillRun()
    {
        Running("moved");

        await sessions.StartAsync("moved", scratch, Token);

        Assert.Equal(root, Record("moved").WorkingDirectory);
        Assert.Contains(reports, line => line.Contains($"run in {root}, not {scratch}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionWhoseTreeWentAwayIsStartedAfresh()
    {
        SessionRecord stale = Running("unmounted");
        host.Mounted.Remove(stale.MountPath);
        host.OnLaunch = (id, _, _) => ServeInBackground(id);

        await sessions.StartAsync("unmounted", root, Token);

        Assert.Equal(1, host.Launches);
        Assert.True(Exited(stale.Pid));
        Assert.NotEqual(stale.Pid, Record("unmounted").Pid);
    }

    /// <summary>
    /// The first line a server prints about why it stopped is the one that matters, and a hook
    /// only sees what start says.
    /// </summary>
    [Fact]
    public async Task AServerThatCannotStartFailsTheStartWithItsOwnReason()
    {
        host.OnLaunch = (_, _, log) =>
        {
            File.WriteAllLines(log, ["terminalfs: settings.json: not found. Run 'terminalfs --init-settings'.",
                .. Enumerable.Range(0, 30).Select(line => $"usage line {line}")]);

            return Process.Start("/bin/sh", ["-c", "exit 1"]);
        };

        MountException refused = await Assert.ThrowsAsync<MountException>(() => sessions.StartAsync("broken", root, Token));

        Assert.Contains("exited with 1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("--init-settings", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("usage line", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(paths.MountPath("broken")));
    }

    /// <summary>
    /// A start that runs out of time may do so just as the mount completes, with nothing recorded
    /// to say so. The mount is found by its path and taken down with the server.
    /// </summary>
    [Fact]
    public async Task AStartThatTimesOutLeavesNoServerAndNoMount()
    {
        var slow = new Sessions(paths, host, reports.Add) { StartTimeout = TimeSpan.FromMilliseconds(300) };
        int server = 0;

        host.OnLaunch = (id, _, _) =>
        {
            // Start disposes what it is handed, so it gets a process this test does not keep.
            Process launched = Process.Start("sleep", ["300"]);
            server = launched.Id;
            Directory.CreateDirectory(paths.MountPath(id));
            host.Mounted[paths.MountPath(id)] = 40999;

            return launched;
        };

        await Assert.ThrowsAsync<MountException>(() => slow.StartAsync("slow", root, Token));

        Assert.NotEqual(0, server);
        Assert.False(IsRunning(server));
        Assert.Empty(host.Mounted);
        Assert.False(Directory.Exists(paths.MountPath("slow")));
    }

    /// <summary>
    /// The session's directory is mounted over as root. A link planted there would move that
    /// mount onto whatever it points at, and hide it.
    /// </summary>
    [Fact]
    public async Task ALinkWhereTheTreeGoesIsRefused()
    {
        Directory.CreateDirectory(root);
        string target = Path.Combine(scratch, "victim");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "keep"), "x", Token);
        Directory.CreateSymbolicLink(paths.MountPath("planted"), target);

        await Assert.ThrowsAsync<MountException>(() => sessions.StartAsync("planted", root, Token));

        Assert.Equal(0, host.Launches);
        Assert.True(File.Exists(Path.Combine(target, "keep")));
    }

    [Fact]
    public async Task TheRuntimeDirectoryIsClosedToEveryoneElse()
    {
        // Skipped on Windows by the constructor; said again for the platform analyzer.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        await sessions.StopAsync("anything", Token);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(root));
    }

    [Fact]
    public async Task StoppingASessionLeavesNothingBehind()
    {
        SessionRecord running = Running("done");

        Assert.True(await sessions.StopAsync("done", Token));

        Assert.True(Exited(running.Pid));
        Assert.Empty(host.Mounted);
        Assert.Equal([paths.LockPath], Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task StoppingWhatIsNotThereIsHarmless() =>
        Assert.False(await sessions.StopAsync("never-started", Token));

    [Fact]
    public async Task StoppingAMountNobodyRecordedUnmountsIt()
    {
        Directory.CreateDirectory(paths.MountPath("unrecorded"));
        host.Mounted[paths.MountPath("unrecorded")] = 40123;

        Assert.True(await sessions.StopAsync("unrecorded", Token));

        Assert.Empty(host.Mounted);
        Assert.False(Directory.Exists(paths.MountPath("unrecorded")));
    }

    [Fact]
    public async Task AServerThatIgnoresTheStopIsKilledWithItsCommands()
    {
        string child = Path.Combine(scratch, "child.pid");
        SessionRecord stubborn = Running("stubborn", "/bin/sh", "-c", $"trap '' TERM; sleep 300 & echo $! > {child}; wait");
        int command = await ReadPidAsync(child);

        await sessions.StopAsync("stubborn", Token);

        Assert.True(Exited(stubborn.Pid));
        Assert.True(SpinWait.SpinUntil(() => !IsRunning(command), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CollectingStopsASessionWhoseServerIsGone()
    {
        SessionRecord dead = Running("dead");
        Kill(dead.Pid);

        int stopped = await sessions.CollectAsync(null, Token);

        Assert.Equal(1, stopped);
        Assert.Empty(host.Mounted);
        Assert.False(File.Exists(paths.RecordPath("dead")));
    }

    /// <summary>
    /// Age is a guess about an agent nothing here can see, so it is only acted on when asked for.
    /// </summary>
    [Fact]
    public async Task CollectingWithoutAnAgeLeavesLiveSessionsAlone()
    {
        SessionRecord old = Running("old", created: DateTimeOffset.UtcNow.AddDays(-30));

        Assert.Equal(0, await sessions.CollectAsync(null, Token));

        Assert.True(Alive(old.Pid));
    }

    [Fact]
    public async Task CollectingByAgeStopsOldSessionsAndLeavesYoungerOnesAlone()
    {
        SessionRecord old = Running("old", created: DateTimeOffset.UtcNow.AddHours(-2));
        SessionRecord young = Running("young");

        int stopped = await sessions.CollectAsync(TimeSpan.FromHours(1), Token);

        Assert.Equal(1, stopped);
        Assert.True(Exited(old.Pid));
        Assert.True(Alive(young.Pid));
        Assert.True(File.Exists(paths.RecordPath("young")));
    }

    /// <summary>
    /// A server killed outright cannot kill its own commands, and nothing else would ever stop
    /// them. They are in its process group, which outlives it.
    /// </summary>
    [Fact]
    public async Task TheCommandsOfAServerThatWasKilledAreKilledWithItsSession()
    {
        Assert.SkipUnless(HasSetsid, "needs setsid");

        string child = Path.Combine(scratch, "child.pid");
        SessionRecord crashed = Running("crashed", "setsid", "/bin/sh", "-c", $"sleep 300 & echo $! > {child}; wait");

        int command = await ReadPidAsync(child);
        started.Single(process => process.Id == crashed.Pid).Kill(entireProcessTree: false);
        Assert.True(Exited(crashed.Pid));
        Assert.True(IsRunning(command));

        await sessions.CollectAsync(null, Token);

        Assert.True(SpinWait.SpinUntil(() => !IsRunning(command), TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A server whose parent never reaps it — a container whose first process is not an init —
    /// stays in the process table as a zombie. It still answers to its pid, and it is still gone.
    /// </summary>
    [Fact]
    public async Task AServerLeftAsAZombieIsGoneAndItsCommandsAreKilled()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && HasSetsid, "zombies are read from /proc");

        string server = Path.Combine(scratch, "server.pid");
        string child = Path.Combine(scratch, "child.pid");

        // The outer shell becomes a sleep that never waits for anything, so the server under it
        // is never reaped once it dies.
        Start("/bin/sh", "-c",
            $"setsid /bin/sh -c 'sleep 300 & echo $! > {child}; echo $$ > {server}; wait' & exec sleep 300");

        int pid = await ReadPidAsync(server);
        int command = await ReadPidAsync(child);
        using Process zombie = Process.GetProcessById(pid);
        DateTimeOffset startedAt = zombie.StartTime.ToUniversalTime();

        zombie.Kill(entireProcessTree: false);
        Assert.True(SpinWait.SpinUntil(() => ProcessTable.Find(pid) is { Zombie: true }, TimeSpan.FromSeconds(5)));

        Directory.CreateDirectory(paths.MountPath("zombie"));
        new SessionRecord("zombie", pid, startedAt, 40777, paths.MountPath("zombie"), root, DateTimeOffset.UtcNow)
            .Write(paths.RecordPath("zombie"));

        Assert.Equal(1, await sessions.CollectAsync(null, Token));

        Assert.DoesNotContain(reports, line => line.Contains("killing it", StringComparison.Ordinal));
        Assert.True(SpinWait.SpinUntil(() => !IsRunning(command), TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// A pid outlives its process. A record left by a server that died must not send a signal to
    /// whatever process was given its number next.
    /// </summary>
    [Fact]
    public async Task AProcessThatReusedAServersPidIsNotStopped()
    {
        Process bystander = Start("sleep", "300");
        Directory.CreateDirectory(root);

        var record = new SessionRecord(
            "reused",
            bystander.Id,
            bystander.StartTime.ToUniversalTime().AddHours(-1),
            Port: 40000,
            paths.MountPath("reused"),
            root,
            DateTimeOffset.UtcNow);
        record.Write(paths.RecordPath("reused"));

        Assert.False(record.ServerIsAlive());
        Assert.False(record.ServerIsGone());

        await sessions.CollectAsync(null, Token);

        Assert.True(Alive(bystander.Id));
    }

    /// <summary>
    /// A record is acted on — signalled, unmounted — so one that does not describe its own session
    /// is discarded rather than trusted. Pid 0 would be this process's own group.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "Id": "bad", "Pid": 0, "ProcessStarted": "2026-01-01T00:00:00Z", "Port": 40000, "MountPath": "MOUNT", "WorkingDirectory": "/", "Created": "2026-01-01T00:00:00Z" }""")]
    [InlineData("""{ "Id": "bad", "Pid": 1, "ProcessStarted": "2026-01-01T00:00:00Z", "Port": 40000, "MountPath": "MOUNT", "WorkingDirectory": "/", "Created": "2026-01-01T00:00:00Z" }""")]
    [InlineData("""{ "Id": "other", "Pid": 99999, "ProcessStarted": "2026-01-01T00:00:00Z", "Port": 40000, "MountPath": "MOUNT", "WorkingDirectory": "/", "Created": "2026-01-01T00:00:00Z" }""")]
    [InlineData("""{ "Id": "bad", "Pid": 99999, "ProcessStarted": "2026-01-01T00:00:00Z", "Port": 40000, "MountPath": "/elsewhere", "WorkingDirectory": "/", "Created": "2026-01-01T00:00:00Z" }""")]
    public async Task ARecordThatDoesNotDescribeItsSessionIsDiscarded(string json)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            paths.RecordPath("bad"),
            json.Replace("MOUNT", paths.MountPath("bad"), StringComparison.Ordinal),
            Token);

        Assert.Equal(0, await sessions.CollectAsync(null, Token));
        Assert.False(File.Exists(paths.RecordPath("bad")));
        Assert.Equal(0, await sessions.CollectAsync(null, Token));
    }

    [Fact]
    public async Task CollectingSweepsWhatNoSessionClaimsButNeverEmptiesADirectory()
    {
        Directory.CreateDirectory(Path.Combine(root, "abandoned"));
        Directory.CreateDirectory(Path.Combine(root, "occupied"));
        await File.WriteAllTextAsync(Path.Combine(root, "occupied", "keep"), "x", Token);
        await File.WriteAllTextAsync(paths.LogPath("gone"), "x", Token);
        await File.WriteAllTextAsync(paths.RecordPath("half") + ".tmp", "{", Token);

        await sessions.CollectAsync(null, Token);

        Assert.False(Directory.Exists(Path.Combine(root, "abandoned")));
        Assert.True(File.Exists(Path.Combine(root, "occupied", "keep")));
        Assert.False(File.Exists(paths.LogPath("gone")));
        Assert.False(File.Exists(paths.RecordPath("half") + ".tmp"));
    }

    [Fact]
    public async Task ASessionCommandWaitsForAnotherAndSaysSoWhenItGivesUp()
    {
        Directory.CreateDirectory(root);
        var impatient = new Sessions(paths, host, reports.Add) { LockTimeout = TimeSpan.FromMilliseconds(300) };

        using (new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            MountException refused = await Assert.ThrowsAsync<MountException>(() => impatient.StopAsync("x", Token));

            Assert.Contains("held", refused.Message, StringComparison.Ordinal);
        }

        Assert.False(await impatient.StopAsync("x", Token));
    }

    public void Dispose()
    {
        foreach (Process process in started)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.Dispose();
        }

        foreach (string directory in (string[])[root, scratch])
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static bool HasSetsid => File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid");

    /// <summary>
    /// What a session's server does once launched: runs, mounts its tree, and writes its record.
    /// </summary>
    private Process ServeInBackground(string id)
    {
        SessionRecord record = Running(id);

        // The process launch returns is the server; start disposes it, so it is a second handle
        // on the same process rather than one of ours.
        return Process.GetProcessById(record.Pid);
    }

    /// <summary>A session whose server is <paramref name="command"/>, mounted and recorded.</summary>
    private SessionRecord Running(string id, params string[] command) => Running(id, null, command);

    private SessionRecord Running(string id, DateTimeOffset? created, params string[] command)
    {
        Process server = command.Length == 0 ? Start("sleep", "300") : Start(command[0], command[1..]);
        Directory.CreateDirectory(paths.MountPath(id));

        var record = new SessionRecord(
            id,
            server.Id,
            server.StartTime.ToUniversalTime(),
            Port: 40000 + started.Count,
            paths.MountPath(id),
            root,
            created ?? DateTimeOffset.UtcNow);

        record.Write(paths.RecordPath(id));
        host.Mounted[record.MountPath] = record.Port;

        return record;
    }

    private SessionRecord Record(string id) =>
        SessionRecord.Read(paths.RecordPath(id)) ?? throw new InvalidOperationException($"no record for {id}");

    private Process Start(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {file}");
        started.Add(process);

        return process;
    }

    private void Kill(int pid)
    {
        Process process = started.Single(candidate => candidate.Id == pid);
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
    }

    /// <summary>Whether a process this test started has gone, allowing a moment for it.</summary>
    private bool Exited(int pid) => started.Single(candidate => candidate.Id == pid).WaitForExit(TimeSpan.FromSeconds(5));

    private bool Alive(int pid) => !started.Single(candidate => candidate.Id == pid).HasExited;

    /// <summary>
    /// Whether a process this test did not start is running. An orphan is reaped by whatever
    /// adopted it, which in a container may be nothing, so a zombie counts as gone.
    /// </summary>
    private static bool IsRunning(int pid) => ProcessTable.Find(pid) is { Zombie: false };

    private static async Task<int> ReadPidAsync(string file)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(file) && int.TryParse((await File.ReadAllTextAsync(file)).Trim(), out int pid))
            {
                return pid;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException($"{file} never named a process");
    }

    private sealed class FakeHost : ISessionHost
    {
        /// <summary>The mount table: mount path to port.</summary>
        internal Dictionary<string, int> Mounted { get; } = [];

        internal int Launches { get; private set; }

        internal Func<string, string, string, Process>? OnLaunch { get; set; }

        public Process Launch(string id, string workingDirectory, string log)
        {
            Launches++;

            return OnLaunch?.Invoke(id, workingDirectory, log)
                ?? throw new InvalidOperationException("nothing should have been launched");
        }

        public Task<int?> MountedPortAsync(string mountPath, CancellationToken cancellationToken) =>
            Task.FromResult(Mounted.TryGetValue(mountPath, out int port) ? port : (int?)null);

        public Task UnmountAsync(string mountPath, int port, CancellationToken cancellationToken)
        {
            Assert.Equal(Mounted[mountPath], port);
            Mounted.Remove(mountPath);

            return Task.CompletedTask;
        }
    }
}
