using System.Text;
using NineP.Client;
using NineP.Protocol;
using TerminalFs.Core;

namespace TerminalFs.Tests;

/// <summary>
/// The tree as a client meets it. Everything here goes over a socket, because what is being
/// checked is the half the model cannot see: that closing the control file is what runs the
/// command, that a read of a growing file ends rather than waiting, and that a refusal arrives
/// as an error a caller can act on.
/// </summary>
public sealed class EndToEndTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<string> ReadAsync(NinePSession session, string path) =>
        Encoding.UTF8.GetString(await session.ReadFileAsync(path, Token));

    /// <summary>Ends a command by writing to its kill file.</summary>
    private static async Task KillAsync(NinePSession session, string id)
    {
        NinePFid fid = await session.OpenFileAsync(
            $"/cmd/{id}/kill", OpenMode.Write, OpenFlags.Truncate, Token);

        await using (fid.ConfigureAwait(false))
        {
            await fid.WriteAllAsync("x"u8.ToArray(), Token);
        }
    }

    /// <summary>Writes one command, as a shell redirect does: create, write, close.</summary>
    /// <remarks>
    /// A create and not an open. A name nobody has taken is not a file under <c>/ctl</c>, which
    /// is what lets a client make one — and what every shell redirect does, since <c>&gt;</c> is
    /// <c>O_WRONLY|O_CREAT|O_TRUNC</c>.
    /// </remarks>
    private static async Task RunAsync(NinePSession session, string id, string text)
    {
        NinePFid fid = await session.CreateFileAsync("/ctl/" + id, cancellationToken: Token);

        await using (fid.ConfigureAwait(false))
        {
            await fid.WriteAllAsync(Encoding.UTF8.GetBytes(text), Token);
        }
    }

    [Fact]
    public async Task ACommandWrittenToCtlRunsOnCloseAndItsOutputIsReadable()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo hello");

        Assert.Equal("completed", (await ReadAsync(session, "/cmd/t1/wait")).Trim());
        Assert.Equal("hello", (await ReadAsync(session, "/cmd/t1/stdout")).Trim());
        Assert.Equal("0", (await ReadAsync(session, "/cmd/t1/exitcode")).Trim());
    }

    /// <summary>
    /// The close is what runs it. Until the fid is clunked the command has been reserved and
    /// nothing more, which is what lets a caller write it in pieces.
    /// </summary>
    [Fact]
    public async Task NothingRunsUntilTheFidIsClunked()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        NinePFid fid = await session.CreateFileAsync("/ctl/t1", cancellationToken: Token);

        await fid.WriteAllAsync(Encoding.UTF8.GetBytes("echo hello"), Token);

        // The name is taken and nothing else: no process, and nothing under /cmd to describe one.
        Assert.NotNull(served.Registry.FindDraft("t1"));
        Assert.Null(served.Registry.Find("t1"));

        await fid.DisposeAsync();

        Assert.Equal("completed", (await ReadAsync(session, "/cmd/t1/wait")).Trim());
    }

    /// <summary>
    /// The bug this change exists for. While every valid name resolved on a walk there was never
    /// anything left to create, so an exclusive create could only ever answer "file exists" — and
    /// an agent harness whose write tool opens a temporary file with <c>O_EXCL</c> could not use
    /// this tree at all.
    /// </summary>
    [Fact]
    public async Task CreatingAControlFileSucceedsForANameNobodyHasTaken()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        NinePFid made = await session.CreateFileAsync("/ctl/t1", cancellationToken: Token);

        await using (made.ConfigureAwait(false))
        {
            Assert.NotNull(served.Registry.FindDraft("t1"));
        }
    }

    [Fact]
    public async Task CreatingAControlFileIsRefusedForANameThatIsTaken()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await (await session.CreateFileAsync("/ctl/t1", cancellationToken: Token)).DisposeAsync();

        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await session.CreateFileAsync("/ctl/t1", cancellationToken: Token));

        Assert.Equal(Errno.EEXIST, refused.Error.Errno);
    }

    /// <summary>
    /// A name created and never written to leaves nothing behind: no directory, no output, no
    /// entry under <c>/cmd</c>. Filling <c>/cmd</c> with probe opens and half-written temporary
    /// files is what this change is about.
    /// </summary>
    [Fact]
    public async Task ANameCreatedAndNeverWrittenLeavesNothingUnderCmd()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await (await session.CreateFileAsync("/ctl/t1", cancellationToken: Token)).DisposeAsync();

        IReadOnlyList<DirEntry> commands = await session.ReadDirAsync("/cmd", Token);

        Assert.Equal(["index.md"], commands.Select(entry => entry.Name));

        // It is a file under /ctl though: a name nobody can see is a name nobody can clean up.
        IReadOnlyList<DirEntry> control = await session.ReadDirAsync("/ctl", Token);

        Assert.Equal(["index.md", "t1"], control.Select(entry => entry.Name));

        await session.RemoveAsync("/ctl/t1", Token);

        Assert.Null(served.Registry.FindDraft("t1"));
    }

    /// <summary>
    /// The documented flow, over a socket, with a real settle window and no clock to move: a
    /// write followed at once by a read of <c>wait</c> must not miss. This is the only place that
    /// race is real.
    /// </summary>
    [Fact]
    public async Task AWriteFollowedAtOnceByAReadOfWaitNeverMisses()
    {
        await using Served served = await Served.StartAsync(settle: TimeSpan.FromSeconds(30));
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo hello");

        Assert.Equal("completed", (await ReadAsync(session, "/cmd/t1/wait")).Trim());
        Assert.Equal("hello", (await ReadAsync(session, "/cmd/t1/stdout")).Trim());
    }

    /// <summary>
    /// The shape this change exists for, end to end: a client writes to a temporary name, closes
    /// it, and renames it into place. Nothing ever runs under the temporary name.
    /// </summary>
    [Fact]
    public async Task WritingToATemporaryNameAndRenamingItRunsItUnderTheFinalName()
    {
        await using Served served = await Served.StartAsync(settle: TimeSpan.FromSeconds(30));
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "build.tmp.87694", "echo hello");

        await session.RenameAsync("/ctl/build.tmp.87694", "/ctl/build", Token);

        Assert.Equal("completed", (await ReadAsync(session, "/cmd/build/wait")).Trim());
        Assert.Equal("hello", (await ReadAsync(session, "/cmd/build/stdout")).Trim());

        Assert.Null(served.Registry.Find("build.tmp.87694"));
        Assert.Null(served.Registry.FindDraft("build.tmp.87694"));
    }

    [Fact]
    public async Task RenamingANameThatHasAlreadyRunIsRefused()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo hello");
        Assert.Equal("completed", (await ReadAsync(session, "/cmd/t1/wait")).Trim());

        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await session.RenameAsync("/ctl/t1", "/ctl/t2", Token));

        Assert.Equal(Errno.ENOENT, refused.Error.Errno);
    }

    /// <summary>
    /// Nothing here can be append-only, exclusive or temporary, and the core removes a file whose
    /// handler gave it none of the flags the create asked for — so the refusal has to come before
    /// the name is taken, not after it has been taken and given back.
    /// </summary>
    [Fact]
    public async Task ACreateAskingForFlagsThisTreeCannotGiveIsRefused()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        NinePFid control = await session.WalkAsync("/ctl", Token);

        await using (control.ConfigureAwait(false))
        {
            NinePException refused = await Assert.ThrowsAsync<NinePException>(
                async () => await control.CreateAsync(
                    "t1",
                    FileKind.File,
                    FilePermissions.AllRead | FilePermissions.AllWrite,
                    OpenMode.Write,
                    OpenFlags.None,
                    FileFlags.Append,
                    Token));

            Assert.Equal(Errno.EOPNOTSUPP, refused.Error.Errno);
        }

        Assert.Null(served.Registry.FindDraft("t1"));
    }

    /// <summary>
    /// A name that has been written to reports what was written, because by then nothing can open
    /// it again and there is no cache left to merge into a command. A client that writes a file
    /// atomically stats it afterwards, and a zero there is a write it reports as having silently
    /// failed — for a command that in fact ran.
    /// </summary>
    [Fact]
    public async Task ANameThatHasBeenWrittenToReportsWhatWasWritten()
    {
        await using Served served = await Served.StartAsync(settle: TimeSpan.FromSeconds(30));
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1.tmp", "echo hello");

        // A name that can still be written to reports nothing, which is what keeps a client from
        // merging its own cache into the command it is about to send.
        await (await session.CreateFileAsync("/ctl/t2", cancellationToken: Token)).DisposeAsync();

        Assert.Equal(0UL, (await session.GetAttrAsync("/ctl/t2", Token)).Size);
        Assert.Equal(10UL, (await session.GetAttrAsync("/ctl/t1.tmp", Token)).Size);

        await session.RenameAsync("/ctl/t1.tmp", "/ctl/t1", Token);

        Assert.Equal(10UL, (await session.GetAttrAsync("/ctl/t1", Token)).Size);
    }

    [Fact]
    public async Task WaitAnswersCompletedAndTheDirectoryThenListsExitCodeNotPid()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo hello");
        await ReadAsync(session, "/cmd/t1/wait");

        string[] names = [.. (await session.ReadDirAsync("/cmd/t1", Token)).Select(entry => entry.Name)];

        Assert.Contains("exitcode", names);
        Assert.DoesNotContain("pid", names);
        Assert.Equal(["command", "status", "exitcode", "stdout", "stderr", "wait"], names);
    }

    [Fact]
    public async Task AMultiLineCommandArrivesWhole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a newline is not a separator in cmd");

        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "for i in 1 2 3; do echo line $i; done\n");
        await ReadAsync(session, "/cmd/t1/wait");

        Assert.Equal(
            ["line 1", "line 2", "line 3"],
            (await ReadAsync(session, "/cmd/t1/stdout")).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A read of output ends at what has arrived rather than waiting for more, which is what
    /// makes these ordinary files: <c>cat</c> ends, and reading again picks up the rest.
    /// </summary>
    [Fact]
    public async Task OutputCanBeReadWhileTheCommandIsStillRunning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sleep is a shell built-in here");

        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo first; sleep 3; echo second");

        string early = string.Empty;

        for (int attempt = 0; attempt < 100 && early.Length == 0; attempt++)
        {
            await Task.Delay(50, Token);
            early = (await ReadAsync(session, "/cmd/t1/stdout")).Trim();
        }

        Assert.Equal("first", early);
        Assert.Equal("running", (await ReadAsync(session, "/cmd/t1/status")).Trim());

        await ReadAsync(session, "/cmd/t1/wait");

        Assert.Equal("first\nsecond", (await ReadAsync(session, "/cmd/t1/stdout")).Trim().ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// A name this tree cannot carry is refused by the create that asked for it, because that is
    /// the message that asked. It does not resolve on a walk either — nothing has taken it — but
    /// answering a create with "no such file" would say nothing about why it cannot be made.
    /// </summary>
    [Fact]
    public async Task ANameTheTreeCannotCarryIsRefusedAtTheCreate()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await RunAsync(session, ".hidden", "echo hello"));

        Assert.Equal(Errno.EINVAL, refused.Error.Errno);
    }

    [Fact]
    /// <summary>
    /// A name already taken is refused as <c>EEXIST</c>, not as "no such file". Answering the
    /// latter would be both untrue and the opposite of the truth: the name is unavailable
    /// precisely because it exists.
    /// </summary>
    public async Task ADuplicateNameFailsTheOpenWithExists()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo one");

        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await RunAsync(session, "t1", "echo two"));

        Assert.Equal(Errno.EEXIST, refused.Error.Errno);
    }

    [Fact]
    public async Task ADeniedCommandFailsTheWriteNamingTheRule()
    {
        await using Served served = await Served.StartAsync(deny: ["Bash(sudo:*)"]);
        await using NinePSession session = await served.ConnectAsync();

        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await RunAsync(session, "t1", "sudo ls"));

        Assert.Equal(Errno.EPERM, refused.Error.Errno);
        Assert.Equal(CommandState.Denied, served.Registry.Find("t1")!.State);
    }

    /// <summary>
    /// The number is all a mount gets: 9P2000.L carries no sentence with an error, so a caller
    /// whose write failed has been told "Operation not permitted" and nothing else. The reason
    /// has to be somewhere they can walk to, and the command they named is that place — three
    /// files, because nothing ran and there is nothing else to say.
    /// </summary>
    [Fact]
    public async Task ADeniedCommandIsADirectoryHoldingWhatWasAskedStatusAndWhy()
    {
        await using Served served = await Served.StartAsync(deny: ["Bash(sudo:*)"]);
        await using NinePSession session = await served.ConnectAsync();

        await Assert.ThrowsAsync<NinePException>(
            async () => await RunAsync(session, "t1", "sudo ls"));

        string[] files = [.. (await session.ReadDirAsync("/cmd/t1", Token)).Select(entry => entry.Name)];

        Assert.Equal(["command", "status", "reason"], files);

        Assert.Equal("denied", (await ReadAsync(session, "/cmd/t1/status")).Trim());
        Assert.Equal("sudo ls", (await ReadAsync(session, "/cmd/t1/command")).Trim());
        Assert.Contains(
            "Bash(sudo:*)", await ReadAsync(session, "/cmd/t1/reason"), StringComparison.Ordinal);

        // Absent rather than empty. An empty stdout would promise output that can never arrive.
        NinePException missing = await Assert.ThrowsAsync<NinePException>(
            async () => await ReadAsync(session, "/cmd/t1/stdout"));

        Assert.Equal(Errno.ENOENT, missing.Error.Errno);
    }

    /// <summary>
    /// A name runs once, and a refused command spent its name like any other. This is the whole
    /// of the recovery: remove the directory, or — quicker — write to a different name.
    /// </summary>
    [Fact]
    public async Task ARefusedNameStaysTakenUntilItsDirectoryIsRemoved()
    {
        await using Served served = await Served.StartAsync(deny: ["Bash(sudo:*)"]);
        await using NinePSession session = await served.ConnectAsync();

        await Assert.ThrowsAsync<NinePException>(
            async () => await RunAsync(session, "t1", "sudo ls"));

        NinePException taken = await Assert.ThrowsAsync<NinePException>(
            async () => await RunAsync(session, "t1", "echo ok"));

        Assert.Equal(Errno.EEXIST, taken.Error.Errno);

        foreach (string file in new[] { "command", "status", "reason" })
        {
            await session.RemoveAsync("/cmd/t1/" + file, Token);
        }

        await session.RemoveAsync("/cmd/t1", Token);

        await RunAsync(session, "t1", "echo ok");

        Assert.Equal("completed", (await ReadAsync(session, "/cmd/t1/wait")).Trim());
    }

    [Fact]
    public async Task KillEndsARunningCommand()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", Sleep(30));

        Command command = served.Registry.Find("t1")!;

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, Token);
        }

        await KillAsync(session, "t1");

        Assert.Equal("error", (await ReadAsync(session, "/cmd/t1/wait")).Trim());
    }

    [Fact]
    public async Task RemovingChildrenThenTheDirectoryTakesItOutOfCmd()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo hello");
        await ReadAsync(session, "/cmd/t1/wait");

        foreach (DirEntry entry in await session.ReadDirAsync("/cmd/t1", Token))
        {
            await session.RemoveAsync("/cmd/t1/" + entry.Name, Token);
        }

        Assert.Empty(await session.ReadDirAsync("/cmd/t1", Token));

        await session.RemoveAsync("/cmd/t1", Token);

        Assert.Null(served.Registry.Find("t1"));
        Assert.DoesNotContain(
            await session.ReadDirAsync("/cmd", Token),
            entry => entry.Name == "t1");
    }

    /// <summary>
    /// The whole of what <c>rm -r</c> does to a command that is still running: unlink every
    /// child, then the directory. Every child has to unlink for that to finish — a refusal on
    /// any one of them leaves the directory not empty, the removal abandoned halfway, and the
    /// command it was meant to stop still running.
    /// </summary>
    [Fact]
    public async Task RemovingARunningCommandChildByChildThenTheDirectoryKillsIt()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", Sleep(30));

        Command command = served.Registry.Find("t1")!;

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, Token);
        }

        foreach (DirEntry entry in await session.ReadDirAsync("/cmd/t1", Token))
        {
            await session.RemoveAsync("/cmd/t1/" + entry.Name, Token);
        }

        await session.RemoveAsync("/cmd/t1", Token);

        Assert.Null(served.Registry.Find("t1"));
        Assert.Equal(CommandState.Error, await command.WaitAsync(TimeSpan.FromSeconds(30), Token));
    }

    /// <summary>
    /// And what a caller who removes the directory outright means. A running command is killed
    /// with it: the directory is the only handle on it.
    /// </summary>
    [Fact]
    public async Task RemovingTheDirectoryOfARunningCommandKillsIt()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", Sleep(30));

        Command command = served.Registry.Find("t1")!;

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, Token);
        }

        await session.RemoveAsync("/cmd/t1", Token);

        Assert.Equal(
            CommandState.Error,
            await command.WaitAsync(TimeSpan.FromSeconds(30), Token));
    }

    [Fact]
    public async Task RemovingANonEmptyDirectoryThroughRmdirIsRefused()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "t1", "echo hello");
        await ReadAsync(session, "/cmd/t1/wait");

        // Tremove carries the kind the client believes it is removing; a file is not a directory.
        NinePException refused = await Assert.ThrowsAsync<NinePException>(
            async () => await session.RemoveAsync("/cmd", Token));

        Assert.NotEqual(0, refused.Error.Errno);
    }

    [Fact]
    public async Task TruncatingCtlOnOpenIsAccepted()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        // What `echo … > ctl/t1` does on a v9fs mount when the name is already there: O_TRUNC
        // arrives as a Tsetattr setting size to zero before a byte is written. A name nobody has
        // taken is created instead, and carries its truncation in the create.
        await (await session.CreateFileAsync("/ctl/t1", cancellationToken: Token)).DisposeAsync();

        NinePFid fid = await session.OpenFileAsync("/ctl/t1", OpenMode.Write, OpenFlags.Truncate, Token);

        await using (fid.ConfigureAwait(false))
        {
            await fid.WriteAllAsync(Encoding.UTF8.GetBytes("echo hello"), Token);
        }

        Assert.Equal("completed", (await ReadAsync(session, "/cmd/t1/wait")).Trim());
    }

    /// <summary>
    /// A control file must report no length, and this is the assertion that keeps it that way. A
    /// client that believes a file has contents treats a write as a modification of them: when
    /// this file answered with help text, macOS smbfs laid the command over the front of its
    /// cached copy and sent back the whole thing, so what ran was the command followed by the
    /// tail of its own help — a parse error in a line nobody wrote.
    /// </summary>
    [Fact]
    public async Task AControlFileReportsNoLength()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await (await session.CreateFileAsync("/ctl/t1", cancellationToken: Token)).DisposeAsync();

        Assert.Equal(0UL, (await session.GetAttrAsync("/ctl/t1", Token)).Size);

        await RunAsync(session, "t2", Sleep(30));

        Assert.Equal(0UL, (await session.GetAttrAsync("/cmd/t2/kill", Token)).Size);

        await KillAsync(session, "t2");
    }

    /// <summary>
    /// The usage moved to a page of its own when the control file stopped being readable: a
    /// client that clamps reads to the stated size gets nothing from a file that says it is
    /// empty, and this one must say so.
    /// </summary>
    [Fact]
    public async Task TheControlIndexSaysHowToRunSomething()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        Assert.Contains("> /ctl/build", await ReadAsync(session, "/ctl/index.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTreeIsWalkableFromTheRoot()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        string[] top = [.. (await session.ReadDirAsync("/", Token)).Select(entry => entry.Name)];

        Assert.Equal(["index.md", "ctl", "cmd", "skills"], top);

        Assert.Contains("Commands, as files", await ReadAsync(session, "/index.md"), StringComparison.Ordinal);
        Assert.Contains("name: terminalfs", await ReadAsync(session, "/skills/terminalfs/SKILL.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Two callers writing at once. Each open is its own fid with its own buffer, so their bytes
    /// never mix; the server dispatches the two concurrently.
    /// </summary>
    [Fact]
    public async Task TwoOpensAtOnceRunTwoCommandsIndependently()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession first = await served.ConnectAsync();
        await using NinePSession second = await served.ConnectAsync();

        NinePFid a = await first.CreateFileAsync("/ctl/p1", cancellationToken: Token);
        NinePFid b = await second.CreateFileAsync("/ctl/p2", cancellationToken: Token);

        await a.WriteAsync(0, Encoding.UTF8.GetBytes("echo "), Token);
        await b.WriteAsync(0, Encoding.UTF8.GetBytes("echo "), Token);
        await a.WriteAsync(0, Encoding.UTF8.GetBytes("one"), Token);
        await b.WriteAsync(0, Encoding.UTF8.GetBytes("two"), Token);

        await a.DisposeAsync();
        await b.DisposeAsync();

        await ReadAsync(first, "/cmd/p1/wait");
        await ReadAsync(second, "/cmd/p2/wait");

        Assert.Equal("one", (await ReadAsync(first, "/cmd/p1/stdout")).Trim());
        Assert.Equal("two", (await ReadAsync(second, "/cmd/p2/stdout")).Trim());
    }

    /// <summary>
    /// A blocked read holds its own fid and nothing else: the server dispatches requests on one
    /// connection concurrently and serialises only per fid. Without that, an agent waiting on one
    /// command could not start another.
    /// </summary>
    [Fact]
    public async Task AWaitDoesNotHoldUpAnythingElseOnTheSameConnection()
    {
        await using Served served = await Served.StartAsync();
        await using NinePSession session = await served.ConnectAsync();

        await RunAsync(session, "slow", Sleep(30));

        Task<string> waiting = ReadAsync(session, "/cmd/slow/wait");

        await RunAsync(session, "quick", "echo hello");

        Assert.Equal("completed", (await ReadAsync(session, "/cmd/quick/wait")).Trim());
        Assert.False(waiting.IsCompleted, "the wait answered before its command had finished");

        served.Registry.Find("slow")!.Kill();

        Assert.Equal("error", (await waiting).Trim());
    }

    private static string Sleep(int seconds) => OperatingSystem.IsWindows()
        ? $"ping -n {seconds + 1} 127.0.0.1 >NUL"
        : $"sleep {seconds}";
}
