using Microsoft.Extensions.Time.Testing;

namespace TerminalFs.Core.Tests;

/// <summary>
/// When a command stops being in the tree. Three things can take it: the caller removing it, the
/// timer expiring it, and the server shutting down — and they can all happen at once, which is
/// what most of this is about.
/// </summary>
public sealed class LifetimeTests : IDisposable
{
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(60);

    private readonly FakeTimeProvider time = new();
    private readonly Workspace workspace;

    public LifetimeTests() =>
        workspace = new Workspace(new CommandOptions { KeepAfterExit = Keep, TimeProvider = time });

    public void Dispose() => workspace.Dispose();

    private CommandRegistry Registry => workspace.Registry;

    /// <summary>
    /// The fake clock does not drive <c>Process</c>, so a command that really runs is waited for
    /// on the real one before time is moved by hand.
    /// </summary>
    private static async Task Finished(Command command)
    {
        for (int attempt = 0; attempt < 600 && !command.HasExited; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(command.HasExited, $"'{command.Id}' did not finish");
    }

    /// <summary>
    /// A denied command is over the moment it is denied, so the clock that clears finished
    /// commands is the clock that clears this one. A second mechanism would be a second thing to
    /// get wrong, and a tree that filled up with refusals nobody read would be the result.
    /// </summary>
    [Fact]
    public void ADeniedCommandIsReapedByTheSameTimerAsAFinishedOne()
    {
        Command command = Denied("t1");

        Assert.Equal(CommandState.Denied, command.State);

        time.Advance(Keep);

        Assert.Null(Registry.Find("t1"));
        Assert.True(command.Retired);
    }

    /// <summary>
    /// The reason is the whole point of keeping the directory, so a reader holding it open must
    /// never have it taken away mid-read. The clock restarts from their close, as it does for
    /// any other command.
    /// </summary>
    [Fact]
    public void AReaderOfADeniedCommandAlwaysGetsToReadIt()
    {
        Command command = Denied("t1");

        Command.Lease reading = command.Open();

        time.Advance(Keep * 3);

        Assert.NotNull(Registry.Find("t1"));

        reading.Dispose();
        time.Advance(Keep);

        Assert.Null(Registry.Find("t1"));
    }

    /// <summary>
    /// Three files are still children, so an <c>rmdir</c> on a denied command is refused for the
    /// same reason it is on a finished one: <c>rm -r</c> unlinks them first.
    /// </summary>
    [Fact]
    public void ADeniedDirectoryWithItsFilesStillInItIsNotEmpty()
    {
        Denied("t1");

        CommandException refused = Assert.Throws<CommandException>(() => Registry.Remove("t1"));

        Assert.Equal(CommandErrno.NotEmpty, refused.Errno);
    }

    /// <summary>
    /// The way out of a refusal by hand, and what frees the name for a reworded command.
    /// </summary>
    [Fact]
    public void UnlinkingTheThreeFilesOfADeniedCommandThenTheDirectoryRemovesIt()
    {
        Command command = Denied("t1");

        foreach (string child in command.VisibleChildren.ToArray())
        {
            Assert.True(command.Hide(child));
        }

        Registry.Remove("t1");

        Assert.Null(Registry.Find("t1"));

        using ControlSession again = Registry.OpenControl("t1");

        Assert.NotNull(Registry.Find("t1"));
    }

    /// <summary>
    /// A command denied without a settings file or a process: closing the control file with
    /// nothing written to it is refused for its shape rather than by a rule.
    /// </summary>
    private Command Denied(string id)
    {
        Registry.OpenControl(id).Close();

        return Registry.Find(id)!;
    }

    [Fact]
    public async Task TheRemovalTimerArmsWhenExitedWithNoOpenHandles()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        Assert.NotNull(Registry.Find("t1"));

        time.Advance(Keep);

        Assert.Null(Registry.Find("t1"));
        Assert.True(command.Retired);
    }

    [Fact]
    public async Task ARunningCommandIsNeverExpired()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        time.Advance(Keep * 10);

        Assert.NotNull(Registry.Find("t1"));

        command.Kill();
        await Finished(command);
    }

    [Fact]
    public async Task AnOpenHandleDisarmsTheTimer()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        using (command.Open())
        {
            time.Advance(Keep * 3);

            Assert.NotNull(Registry.Find("t1"));
        }

        time.Advance(Keep);

        Assert.Null(Registry.Find("t1"));
    }

    [Fact]
    public async Task TheTimerRestartsFromTheLastCloseNotTheExit()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        Command.Lease lease = command.Open();
        time.Advance(Keep * 2);
        lease.Dispose();

        // The clock has already passed the keep time several times over, but the wait starts
        // again when the handle closes.
        time.Advance(Keep - TimeSpan.FromSeconds(1));
        Assert.NotNull(Registry.Find("t1"));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(Registry.Find("t1"));
    }

    [Fact]
    public async Task RemovingARunningCommandKillsIt()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Registry.RemoveTree("t1");

        await Finished(command);

        Assert.Null(Registry.Find("t1"));
        Assert.Equal(CommandState.Error, command.State);
    }

    [Fact]
    public async Task RemovingADirectoryWithVisibleChildrenIsNotEmpty()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        CommandException refused = Assert.Throws<CommandException>(() => Registry.Remove("t1"));

        Assert.Equal(CommandErrno.NotEmpty, refused.Errno);
        Assert.NotNull(Registry.Find("t1"));
    }

    [Fact]
    public async Task UnlinkingEveryChildThenTheDirectoryRemovesIt()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        foreach (string child in command.VisibleChildren.ToArray())
        {
            Assert.True(command.Hide(child), $"'{child}' was not there to unlink");
        }

        Assert.Empty(command.VisibleChildren);

        Registry.Remove("t1");

        Assert.Null(Registry.Find("t1"));
    }

    [Fact]
    public async Task UnlinkingAChildThatIsNotThereSaysSo()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        Assert.False(command.Hide("nonesuch"));
        Assert.True(command.Hide("stdout"));
        Assert.False(command.Hide("stdout"));
    }

    /// <summary>
    /// A caller taking a directory apart one file at a time must not have it taken out from under
    /// them halfway: the rest of their removal would fail on files that were there a moment ago.
    /// </summary>
    [Fact]
    public async Task UnlinkingAChildPinsTheCommandAgainstTheTimer()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        Assert.True(command.Hide("stdout"));

        time.Advance(Keep * 10);

        Assert.NotNull(Registry.Find("t1"));
    }

    /// <summary>
    /// The other half of the same race: the timer got there first, and the caller's next request
    /// names something that is already gone. They asked for it to be gone, and it is.
    /// </summary>
    [Fact]
    public async Task RemovingAnIdTheTimerJustRetiredSucceeds()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        time.Advance(Keep);

        Assert.Null(Registry.Find("t1"));

        Registry.Remove("t1");
        Registry.RemoveTree("t1");
        Assert.True(command.Hide("stdout"));
    }

    [Fact]
    public void RemovingSomethingThatWasNeverThereIsNotFound()
    {
        CommandException refused = Assert.Throws<CommandException>(() => Registry.RemoveTree("nonesuch"));

        Assert.Equal(CommandErrno.NotFound, refused.Errno);
    }

    [Fact]
    public async Task AnIdIsOneCommandAndASecondRunWithItIsRefused()
    {
        Command command = workspace.Run("t1", "echo one");
        await Finished(command);

        CommandException refused = Assert.Throws<CommandException>(() => Registry.Reserve("t1"));

        Assert.Equal(CommandErrno.Exists, refused.Errno);
        Assert.Contains("runs once", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And once it is gone the id is free again — but what comes back is a new command with empty
    /// output, not more of the old one.
    /// </summary>
    [Fact]
    public async Task AnIdIsFreeAgainOnceItsCommandIsRemoved()
    {
        Command first = workspace.Run("t1", "echo one");
        await Finished(first);

        Registry.RemoveTree("t1");

        Command second = workspace.Run("t1", "echo two");
        await Finished(second);

        Assert.NotSame(first, second);
        Assert.Equal("two", Workspace.Read(second.Stdout).Trim());
    }

    /// <summary>
    /// A caller can remove a command while another reader has one of its files open. The command
    /// leaves the tree at once — that is what was asked for — but the bytes on disk stay until
    /// the reader is done with them, because taking them away mid-read is a truncated answer
    /// rather than an error the reader can act on.
    /// </summary>
    [Fact]
    public async Task DiskCleanupWaitsForTheLastHandle()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        using (Command.Lease held = command.Open())
        {
            Registry.RemoveTree("t1");

            Assert.Null(Registry.Find("t1"));
            Assert.True(Directory.Exists(command.Directory), "the directory went while it was being read");
        }

        Assert.False(Directory.Exists(command.Directory), "the directory outlived the last handle");
    }

    [Fact]
    public async Task ReadingACommandThatIsGoneIsNotFound()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Finished(command);

        Registry.RemoveTree("t1");

        CommandException refused = Assert.Throws<CommandException>(() => command.Stdout.OpenRead());

        Assert.Equal(CommandErrno.NotFound, refused.Errno);
    }

    [Fact]
    public async Task TheRegistryRevisionMovesOnAddRemoveAndStateChange()
    {
        uint before = Registry.Revision;

        Command command = workspace.Run("t1", "echo hello");
        uint reserved = Registry.Revision;
        Assert.True(reserved > before, "reserving a command did not move the revision");

        await Finished(command);
        Registry.RemoveTree("t1");

        Assert.True(Registry.Revision > reserved, "removing a command did not move the revision");
    }

    [Fact]
    public async Task DisposingTheRegistryKillsWhatIsRunningAndDeletesTheOutputRoot()
    {
        var workspace = new Workspace(new CommandOptions { KeepAfterExit = Keep, TimeProvider = time });
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        string root = workspace.Registry.OutputRoot;

        workspace.Registry.Dispose();
        await Finished(command);

        Assert.False(Directory.Exists(root), "the output root outlived the registry");

        workspace.Dispose();
    }
}
