using System.Diagnostics;
using TerminalFs.Internal.Mount;
using TerminalFs.Internal.Sessions;

namespace TerminalFs.Tests;

/// <summary>
/// Per-session trees: started once, stopped completely, and collected when nobody stopped them.
/// </summary>
/// <remarks>
/// A real process stands in for each session's server, because what is being pinned is how
/// processes are found, stopped and told apart. The mount is the one thing faked: this suite runs
/// where nothing can be mounted, and whether a mount is ours is pinned by
/// <see cref="MountGuardTests"/>.
/// </remarks>
public sealed class SessionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "terminalfs-sessions-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost host = new();
    private readonly List<Process> started = [];
    private readonly SessionPaths paths;
    private readonly Sessions sessions;

    public SessionTests()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sessions are started and stopped with POSIX signals");

        paths = new SessionPaths(root);
        sessions = new Sessions(paths, host, _ => { })
        {
            StartTimeout = TimeSpan.FromSeconds(10),
            StopTimeout = TimeSpan.FromSeconds(1),
        };
    }

    [Fact]
    public async Task StartingASessionReturnsTheTreeItsServerMounted()
    {
        host.OnLaunch = (id, _, _) => ServeInBackground(id);

        string path = await sessions.StartAsync("s1", root, TestContext.Current.CancellationToken);

        Assert.Equal(paths.MountPath("s1"), path);
        Assert.Equal(1, host.Launches);
    }

    [Fact]
    public async Task TwoSessionsGetSeparateTrees()
    {
        host.OnLaunch = (id, _, _) => ServeInBackground(id);

        string first = await sessions.StartAsync("one", root, TestContext.Current.CancellationToken);
        string second = await sessions.StartAsync("two", root, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.NotEqual(Record("one").Pid, Record("two").Pid);
    }

    [Fact]
    public async Task StartingASessionThatIsAlreadyMountedHandsBackTheSameTree()
    {
        SessionRecord running = Running("again");

        string path = await sessions.StartAsync("again", root, TestContext.Current.CancellationToken);

        Assert.Equal(running.MountPath, path);
        Assert.Equal(0, host.Launches);
    }

    [Fact]
    public async Task ASessionWhoseTreeWentAwayIsStartedAfresh()
    {
        SessionRecord stale = Running("unmounted");
        host.Mounted.Remove(stale.MountPath);
        host.OnLaunch = (id, _, _) => ServeInBackground(id);

        await sessions.StartAsync("unmounted", root, TestContext.Current.CancellationToken);

        Assert.Equal(1, host.Launches);
        Assert.True(Exited(stale.Pid));
        Assert.NotEqual(stale.Pid, Record("unmounted").Pid);
    }

    [Fact]
    public async Task AServerThatCannotMountFailsTheStartWithWhatItSaid()
    {
        host.OnLaunch = (_, _, log) =>
        {
            File.WriteAllText(log, "terminalfs: could not mount: permission denied\n");

            return Process.Start("/bin/sh", ["-c", "exit 3"]);
        };

        MountException refused = await Assert.ThrowsAsync<MountException>(
            () => sessions.StartAsync("broken", root, TestContext.Current.CancellationToken));

        Assert.Contains("exited with 3", refused.Message, StringComparison.Ordinal);
        Assert.Contains("permission denied", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(paths.MountPath("broken")));
    }

    [Fact]
    public async Task StoppingASessionLeavesNothingBehind()
    {
        SessionRecord running = Running("done");

        Assert.True(await sessions.StopAsync("done", TestContext.Current.CancellationToken));

        Assert.True(Exited(running.Pid));
        Assert.Empty(host.Mounted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public async Task StoppingWhatIsNotThereIsHarmless() =>
        Assert.False(await sessions.StopAsync("never-started", TestContext.Current.CancellationToken));

    [Fact]
    public async Task AServerThatIgnoresTheStopIsKilledWithItsCommands()
    {
        SessionRecord stubborn = Running("stubborn", "/bin/sh", "-c", "trap '' TERM; sleep 300 & wait");

        await sessions.StopAsync("stubborn", TestContext.Current.CancellationToken);

        Assert.True(Exited(stubborn.Pid));
    }

    [Fact]
    public async Task CollectingStopsASessionWhoseServerIsGone()
    {
        SessionRecord dead = Running("dead");
        Kill(dead.Pid);

        int stopped = await sessions.CollectAsync(TimeSpan.FromDays(1), TestContext.Current.CancellationToken);

        Assert.Equal(1, stopped);
        Assert.Empty(host.Mounted);
        Assert.False(File.Exists(paths.RecordPath("dead")));
    }

    [Fact]
    public async Task CollectingStopsOldSessionsAndLeavesYoungerOnesAlone()
    {
        SessionRecord old = Running("old", created: DateTimeOffset.UtcNow.AddHours(-2));
        SessionRecord young = Running("young");

        int stopped = await sessions.CollectAsync(TimeSpan.FromHours(1), TestContext.Current.CancellationToken);

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
        Assert.SkipUnless(File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid"), "needs setsid");

        string child = Path.Combine(root, "child.pid");
        Directory.CreateDirectory(root);
        SessionRecord crashed = Running("crashed", "setsid", "/bin/sh", "-c", $"sleep 300 & echo $! > {child}; wait");

        int command = await ReadPidAsync(child);
        started.Single(process => process.Id == crashed.Pid).Kill(entireProcessTree: false);
        Assert.True(Exited(crashed.Pid));
        Assert.True(Running(command));

        await sessions.CollectAsync(TimeSpan.FromDays(1), TestContext.Current.CancellationToken);

        Assert.True(SpinWait.SpinUntil(() => !Running(command), TimeSpan.FromSeconds(5)));
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

        await sessions.CollectAsync(TimeSpan.FromDays(1), TestContext.Current.CancellationToken);

        Assert.True(Alive(bystander.Id));
    }

    [Fact]
    public async Task CollectingRemovesEmptyDirectoriesNoSessionClaimsButNeverEmptiesOne()
    {
        Directory.CreateDirectory(Path.Combine(root, "abandoned"));
        Directory.CreateDirectory(Path.Combine(root, "occupied"));
        await File.WriteAllTextAsync(Path.Combine(root, "occupied", "keep"), "x", TestContext.Current.CancellationToken);

        await sessions.CollectAsync(TimeSpan.FromDays(1), TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Combine(root, "abandoned")));
        Assert.True(File.Exists(Path.Combine(root, "occupied", "keep")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData(".hidden")]
    [InlineData("has space")]
    public void AnIdThatCouldLeaveTheRuntimeDirectoryIsRefused(string id) =>
        Assert.False(SessionPaths.IsValidId(id));

    [Theory]
    [InlineData("0b5c3f5e-6a8f-4a55-9c1e-2d6c1c1c9f10")]
    [InlineData("ses_2a9f.v1")]
    public void TheIdsAgentsUseAreAccepted(string id) =>
        Assert.True(SessionPaths.IsValidId(id));

    [Fact]
    public void SessionsLiveInTheRuntimeDirectory() =>
        Assert.Equal(
            "/run/user/1000/terminalfs",
            SessionPaths.Resolve(Environment(("XDG_RUNTIME_DIR", "/run/user/1000")), "/home/me").Root);

    [Fact]
    public void WithoutARuntimeDirectorySessionsLiveInTheCache()
    {
        Assert.Equal("/home/me/.cache/terminalfs", SessionPaths.Resolve(Environment(), "/home/me").Root);
        Assert.Equal(
            "/var/cache/me/terminalfs",
            SessionPaths.Resolve(Environment(("XDG_CACHE_HOME", "/var/cache/me")), "/home/me").Root);
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

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name == name).Value;

    /// <summary>
    /// What a session's server does once launched: runs, mounts its tree, and writes its record.
    /// </summary>
    private Process ServeInBackground(string id)
    {
        Running(id);

        // The process start returns stands for the shell that launched the server, which has
        // handed over and gone. It is the caller's to dispose, so it is not one of ours.
        return Process.Start("/bin/sh", ["-c", "exit 0"]);
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
            Port: 40000 + host.Mounted.Count,
            paths.MountPath(id),
            root,
            created ?? DateTimeOffset.UtcNow);

        record.Write(paths.RecordPath(id));
        host.Mounted.Add(record.MountPath);

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

    /// <summary>Whether the process has gone, allowing a moment for it to be reaped.</summary>
    private bool Exited(int pid)
    {
        Process process = started.Single(candidate => candidate.Id == pid);

        return process.WaitForExit(TimeSpan.FromSeconds(5));
    }

    private bool Alive(int pid) => !started.Single(candidate => candidate.Id == pid).HasExited;

    /// <summary>
    /// Whether a process this test did not start is running. An orphan is reaped by whatever
    /// adopted it, which in a container may be nothing, so a zombie counts as gone.
    /// </summary>
    private static bool Running(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");

            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

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
        internal HashSet<string> Mounted { get; } = [];

        internal int Launches { get; private set; }

        internal Func<string, string, string, Process>? OnLaunch { get; set; }

        public Process Launch(string id, string workingDirectory, string log)
        {
            Launches++;

            return OnLaunch?.Invoke(id, workingDirectory, log)
                ?? throw new InvalidOperationException("nothing should have been launched");
        }

        public Task<bool> IsMountedAsync(SessionRecord record, CancellationToken cancellationToken) =>
            Task.FromResult(Mounted.Contains(record.MountPath));

        public Task UnmountAsync(SessionRecord record, CancellationToken cancellationToken)
        {
            Mounted.Remove(record.MountPath);

            return Task.CompletedTask;
        }
    }
}
