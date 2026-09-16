namespace TerminalFs.Core.Tests;

/// <summary>
/// What a command does when it runs: where its output goes, what its state says, and what a
/// caller can tell from the directory without reading anything.
/// </summary>
public sealed class CommandTests : IDisposable
{
    private readonly Workspace workspace = new();

    public void Dispose() => workspace.Dispose();

    [Fact]
    public async Task EchoCompletesWithItsOutputAndExitCodeZero()
    {
        Command command = workspace.Run("t1", "echo hello");

        Assert.Equal(CommandState.Completed, await Workspace.Finished(command));
        Assert.Equal(0, command.ExitCode);
        Assert.Equal("hello", Workspace.Read(command.Stdout).Trim());
    }

    [Fact]
    public async Task StandardErrorIsCapturedSeparately()
    {
        Command command = workspace.Run("t1", "echo out; echo err 1>&2");

        await Workspace.Finished(command);

        Assert.Equal("out", Workspace.Read(command.Stdout).Trim());
        Assert.Equal("err", Workspace.Read(command.Stderr).Trim());
    }

    [Fact]
    public async Task ANonZeroExitIsAnError()
    {
        Command command = workspace.Run("t1", "exit 3");

        Assert.Equal(CommandState.Error, await Workspace.Finished(command));
        Assert.Equal(3, command.ExitCode);
        Assert.Equal("error\n", command.StatusLine);
    }

    /// <summary>
    /// The directory is the answer to "is it still going". A caller who can see <c>pid</c> knows
    /// there is a process; one who can see <c>exitcode</c> knows there is not.
    /// </summary>
    [Fact]
    public async Task PidIsListedOnlyWhileRunningAndExitCodeOnlyAfter()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Contains("pid", command.VisibleChildren);
        Assert.DoesNotContain("exitcode", command.VisibleChildren);
        Assert.Equal("running\n", command.StatusLine);

        command.Kill();
        await Workspace.Finished(command);

        Assert.DoesNotContain("pid", command.VisibleChildren);
        Assert.Contains("exitcode", command.VisibleChildren);
        Assert.Null(command.Pid);
    }

    [Fact]
    public async Task KillEndsARunningCommandAsAnError()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        workspace.Registry.Kill("t1");

        Assert.Equal(CommandState.Error, await Workspace.Finished(command));

        // Not the number: a killed process exits -1 on Windows and 137 on Unix, and what a
        // caller needs is that it did not succeed.
        Assert.NotNull(command.ExitCode);
        Assert.NotEqual(0, command.ExitCode);
    }

    [Fact]
    public async Task WaitReturnsRunningWhenTheTimeoutElapsesFirst()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        CommandState state = await command.WaitAsync(
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);

        Assert.True(state is CommandState.Reserved or CommandState.Running);
        Assert.False(command.HasExited);

        command.Kill();
    }

    [Fact]
    public async Task WaitReturnsTheFinalStateAfterExit()
    {
        Command command = workspace.Run("t1", "exit 0");

        Assert.Equal(CommandState.Completed, await Workspace.Finished(command));

        // And again, now that there is nothing to wait for.
        Assert.Equal(
            CommandState.Completed,
            await command.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
    }

    /// <summary>A flushed read is a caller who has gone; the wait goes with them.</summary>
    [Fact]
    public async Task WaitHonoursCancellation()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        using var cancellation = new CancellationTokenSource();
        Task<CommandState> waiting = command.WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        command.Kill();
    }

    [Fact]
    public async Task AShellThatCannotBeStartedIsAnErrorWithTheReasonOnStandardError()
    {
        using var workspace = new Workspace(new CommandOptions { Shell = "/nonexistent/shell" });

        Command command = workspace.Run("t1", "echo hello");

        Assert.Equal(CommandState.Error, await Workspace.Finished(command));
        Assert.Contains("/nonexistent/shell", Workspace.Read(command.Stderr), StringComparison.Ordinal);
        Assert.Equal(-1, command.ExitCode);
    }

    [Fact]
    public async Task OutputRevisionMovesAsBytesArrive()
    {
        Command command = workspace.Run("t1", "echo hello");

        await Workspace.Finished(command);

        Assert.True(command.Stdout.Revision > 0, "nothing was recorded as written");
        Assert.Equal(Workspace.Read(command.Stdout).Length, command.Stdout.Length);
    }

    [Fact]
    public void TheCommandHoldsTheTextAsWritten()
    {
        Command command = workspace.Run("t1", "echo one\necho two");

        Assert.Equal("echo one\necho two", command.Text);
    }

    [Fact]
    public async Task AMultiLineCommandRunsEveryLine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a newline is not a separator in cmd");

        Command command = workspace.Run("t1", "echo one\necho two\n");

        await Workspace.Finished(command);

        Assert.Equal(["one", "two"], Workspace.Read(command.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Asked through the filesystem rather than by comparing what <c>pwd</c> printed: on macOS a
    /// temporary directory is reached through a symlink and a process reports the path it
    /// resolves to, so two spellings of the same directory would fail a string comparison. A
    /// relative read only succeeds from the right directory, whatever it is called.
    /// </summary>
    [Fact]
    public async Task CommandsRunInTheDirectoryTheyWereGiven()
    {
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Root, "marker"),
            "here",
            TestContext.Current.CancellationToken);

        Command command = workspace.Run("t1", OperatingSystem.IsWindows() ? "type marker" : "cat marker");

        Assert.Equal(CommandState.Completed, await Workspace.Finished(command));
        Assert.Equal("here", Workspace.Read(command.Stdout).Trim());
    }

    /// <summary>
    /// There is no channel a caller could type into, so standard input is closed rather than
    /// left open: anything reading it gets end-of-file at once instead of waiting for bytes that
    /// can never arrive.
    /// </summary>
    [Fact]
    public async Task ACommandThatReadsStandardInputSeesEndOfFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "cat is not cmd's");

        Command command = workspace.Run("t1", "cat; echo done");

        Assert.Equal(CommandState.Completed, await Workspace.Finished(command));
        Assert.Equal("done", Workspace.Read(command.Stdout).Trim());
    }

    [Fact]
    public async Task ReadingPastTheCurrentLengthIsEndOfFileNotAWait()
    {
        Command command = workspace.Run("t1", "echo hello");

        await Workspace.Finished(command);

        using Stream stream = command.Stdout.OpenRead();

        stream.Seek(0, SeekOrigin.End);

        byte[] buffer = new byte[64];

        Assert.Equal(0, await stream.ReadAsync(buffer, TestContext.Current.CancellationToken));
    }
}
