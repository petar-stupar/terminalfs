using System.Text;
using TerminalFs.Core.Permissions;

namespace TerminalFs.Core.Tests;

/// <summary>
/// One open of one command's control file, from the first byte to the close that runs it. The
/// write is where a caller sees a refusal — a shell prints the error from the write and nothing
/// from the close — so as much as can be decided early is decided early.
/// </summary>
public sealed class ControlSessionTests : IDisposable
{
    private readonly Workspace workspace = new();

    public void Dispose() => workspace.Dispose();

    private CommandRegistry Registry => workspace.Registry;

    private static void Write(ControlSession session, string text) =>
        session.Write(Encoding.UTF8.GetBytes(text));

    private static async Task<CommandState> Finished(Command command) =>
        await command.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    /// <summary>
    /// Making the file is what takes the name, so two callers racing for one resolve at the
    /// moment they collide rather than after both have written. Nothing appears under
    /// <c>/cmd</c>: there is no command yet, and a name nobody writes one for leaves nothing.
    /// </summary>
    [Fact]
    public void TheNameIsTakenWhenTheFileIsMade()
    {
        using ControlSession session = workspace.Take("t1");

        Assert.NotNull(Registry.FindDraft("t1"));
        Assert.Equal("t1", session.Draft.Name);
        Assert.Null(Registry.Find("t1"));
    }

    [Fact]
    public void AnInvalidNameIsRefusedAtTheCreate()
    {
        CommandException refused = Assert.Throws<CommandException>(() => workspace.Take("a/b"));

        Assert.Equal(CommandErrno.InvalidArgument, refused.Errno);
    }

    /// <summary>
    /// Nothing runs until the file is closed, because until then the command is not finished
    /// being written: the next write could add another line to it.
    /// </summary>
    [Fact]
    public void NothingRunsBeforeClose()
    {
        using ControlSession session = workspace.Take("t1");

        Write(session, "echo hello");

        Assert.Null(Registry.Find("t1"));
        Assert.False(session.Draft.Decided);
    }

    [Fact]
    public async Task ClosingRunsWhatWasWritten()
    {
        ControlSession session = workspace.Take("t1");
        Write(session, "echo hello");
        session.Close();

        Command command = Registry.Find("t1")!;

        Assert.Equal(CommandState.Completed, await Finished(command));
        Assert.Equal("hello", Workspace.Read(command.Stdout).Trim());
    }

    [Fact]
    public void ADuplicateNameIsRefusedWithExists()
    {
        using ControlSession first = workspace.Take("t1");

        CommandException refused = Assert.Throws<CommandException>(() => workspace.Take("t1"));


        Assert.Equal(CommandErrno.Exists, refused.Errno);
    }

    [Fact]
    public async Task ABodySplitAcrossWritesIsJoined()
    {
        ControlSession session = workspace.Take("t1");

        Write(session, "echo ");
        Write(session, "hello ");
        Write(session, "world");
        session.Close();

        Command command = Registry.Find("t1")!;
        await Finished(command);

        Assert.Equal("echo hello world", command.Text);
        Assert.Equal("hello world", Workspace.Read(command.Stdout).Trim());
    }

    /// <summary>
    /// A character split across two writes must survive. Decoding each write on its own would
    /// turn the halves into replacement characters, and the command would run with the wrong text
    /// rather than fail.
    /// </summary>
    [Fact]
    public void ACharacterSplitAcrossTwoWritesSurvives()
    {
        ControlSession session = workspace.Take("t1");

        byte[] text = Encoding.UTF8.GetBytes("echo héllo");

        session.Write(text.AsSpan(0, 7));
        session.Write(text.AsSpan(7));
        session.Close();

        Assert.Equal("echo héllo", Registry.Find("t1")!.Text);
    }

    [Fact]
    public void MoreThanTheLimitIsRefusedWithTooLarge()
    {
        using var small = new Workspace(new CommandOptions { MaxControlBytes = 64 });
        using ControlSession session = small.Take("t1");

        CommandException refused = Assert.Throws<CommandException>(
            () => Write(session, new string('x', 200)));

        Assert.Equal(CommandErrno.TooLarge, refused.Errno);

        Command command = small.Registry.Find("t1")!;

        Assert.Equal(CommandState.Denied, command.State);
        Assert.Contains("64 bytes", command.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A close with nothing written decides nothing and keeps the name, so a client that makes a
    /// file before it writes to it — which is what an agent harness's write tool does — can come
    /// back and write the command. Nothing appears under <c>/cmd</c> in between.
    /// </summary>
    [Fact]
    public void CloseWithoutABodyKeepsTheNameAndDecidesNothing()
    {
        workspace.Take("t1").Close();

        Assert.NotNull(Registry.FindDraft("t1"));
        Assert.False(Registry.FindDraft("t1")!.Decided);
        Assert.Null(Registry.Find("t1"));
    }

    [Fact]
    public async Task TheCloseThatCarriesBytesIsTheOneThatRuns()
    {
        workspace.Take("t1").Close();

        using (ControlSession again = Registry.OpenControl(Registry.FindDraft("t1")!, claiming: false))
        {
            Write(again, "echo hello");
        }

        Command command = Registry.Find("t1")!;

        Assert.Equal(CommandState.Completed, await Finished(command));
        Assert.Equal("hello", Workspace.Read(command.Stdout).Trim());
    }

    /// <summary>
    /// Whitespace is not nothing. A caller who wrote a newline said something, and what they said
    /// is not a command, so it is denied as any command that never started is — with the reason
    /// in <c>reason</c> rather than on a <c>stderr</c> no process ever wrote to.
    /// </summary>
    [Fact]
    public void AWhitespaceOnlyCommandIsDeniedRatherThanKept()
    {
        workspace.Write("t1", "  \n");

        Command command = Registry.Find("t1")!;

        Assert.Equal(CommandState.Denied, command.State);
        Assert.Contains("no command", command.Reason!, StringComparison.Ordinal);
        Assert.Equal(["command", "status", "reason"], command.VisibleChildren);
        Assert.Equal(string.Empty, Workspace.Read(command.Stderr));
    }

    [Fact]
    public void ADeniedCommandFailsTheWriteNamingTheRule()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");
        using ControlSession session = denied.Take("t1");

        CommandException refused = Assert.Throws<CommandException>(() => Write(session, "sudo ls"));

        Assert.Equal(CommandErrno.NotPermitted, refused.Errno);
        Assert.Contains("Bash(sudo:*)", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The name is spent, because the reason has to live somewhere and that somewhere is the
    /// directory the name owns. Removing it is what frees the name again.
    /// </summary>
    [Fact]
    public void ADeniedCommandKeepsItsNameAndItsDirectory()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");

        using (ControlSession session = denied.Take("t1"))
        {
            Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        }

        Assert.Equal(CommandState.Denied, denied.Registry.Find("t1")!.State);

        CommandException taken = Assert.Throws<CommandException>(
            () => denied.Take("t1"));

        Assert.Equal(CommandErrno.Exists, taken.Errno);

        denied.Registry.RemoveTree("t1");

        using ControlSession again = denied.Take("t1");

        Assert.NotNull(denied.Registry.FindDraft("t1"));
    }

    /// <summary>
    /// The text that was refused is what <c>command</c> reports, so a caller can see what the
    /// rule matched against rather than having to remember what they wrote.
    /// </summary>
    [Fact]
    public void ADeniedCommandKeepsWhatWasWrittenInItsCommandFile()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");

        using (ControlSession session = denied.Take("t1"))
        {
            Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        }

        Command command = denied.Registry.Find("t1")!;

        Assert.Equal("sudo ls", command.Text);
        Assert.Contains("Bash(sudo:*)", command.Reason!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing ran, so every file that would describe a process is absent rather than empty: an
    /// empty <c>stdout</c> would promise output that can never arrive.
    /// </summary>
    [Fact]
    public void ADeniedCommandHasOnlyItsCommandStatusAndReason()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");

        using (ControlSession session = denied.Take("t1"))
        {
            Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        }

        Command command = denied.Registry.Find("t1")!;

        Assert.Equal(["command", "status", "reason"], command.VisibleChildren);
        Assert.Null(command.ExitCode);
        Assert.Null(command.Pid);
        Assert.Equal(string.Empty, Workspace.Read(command.Stderr));
        Assert.Equal("denied\n", command.StatusLine);
    }

    /// <summary>
    /// A command can only reach a denied shape at the end: the first write says <c>cd /tmp</c>
    /// and the second adds <c>&amp;&amp; sudo ls</c>.
    /// </summary>
    [Fact]
    public void ADenialSeenOnlyAtTheSecondWriteStillStopsIt()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");

        using (ControlSession session = denied.Take("t1"))
        {
            Write(session, "cd /tmp");

            Assert.Throws<CommandException>(() => Write(session, " && sudo ls"));
        }

        Command command = denied.Registry.Find("t1")!;

        Assert.Equal(CommandState.Denied, command.State);

        // The whole of what was written, not just the write that tripped the rule.
        Assert.Equal("cd /tmp && sudo ls", command.Text);
    }

    [Fact]
    public void AMentionOfADeniedWordIsNotADeniedCommand()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");
        using ControlSession session = denied.Take("t1");

        Write(session, "echo sudo is only text");

        Assert.False(session.Draft.Decided);
    }

    /// <summary>
    /// A mount retries a write that failed. Answering the second attempt with something about the
    /// state this session is now in would describe the machinery rather than the mistake, and
    /// marking the command a second time would overwrite the reason it was refused for.
    /// </summary>
    [Fact]
    public void ARetriedWriteIsRefusedWithTheSameReason()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");
        using ControlSession session = denied.Take("t1");

        CommandException first = Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        CommandException again = Assert.Throws<CommandException>(() => Write(session, "sudo ls"));

        Assert.Equal(first.Message, again.Message);
        Assert.Equal(first.Message, denied.Registry.Find("t1")!.Reason);
    }

    /// <summary>
    /// The rules are consulted again when the file closes, and this is the last moment at which
    /// refusing still means it never ran — a deny list reloaded mid-write gets here. It has to
    /// produce the same shape as a refusal caught at the write, or a caller meets two different
    /// answers to one question.
    /// </summary>
    [Fact]
    public void ACommandTheRulesRefuseOnlyAtCloseIsDeniedRatherThanRun()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");

        Command command = denied.Run("t1", "sudo ls");

        Assert.Equal(CommandState.Denied, command.State);
        Assert.Contains("Bash(sudo:*)", command.Reason!, StringComparison.Ordinal);
        Assert.Equal(["command", "status", "reason"], command.VisibleChildren);

        // Never a process, and never an error: MarkFailed would have left both behind.
        Assert.Null(command.Pid);
        Assert.Null(command.ExitCode);
        Assert.Equal(string.Empty, Workspace.Read(command.Stderr));
    }

    /// <summary>
    /// Two callers writing at once. Each has its own file and its own session, which is the whole
    /// reason the name is the path rather than a word inside one file.
    /// </summary>
    [Fact]
    public async Task TwoOpensAtOnceRunTwoCommandsIndependently()
    {
        ControlSession first = workspace.Take("p1");
        ControlSession second = workspace.Take("p2");

        Write(first, "echo ");
        Write(second, "echo ");
        Write(first, "one");
        Write(second, "two");

        first.Close();
        second.Close();

        Command a = Registry.Find("p1")!;
        Command b = Registry.Find("p2")!;

        await Finished(a);
        await Finished(b);

        Assert.Equal("one", Workspace.Read(a.Stdout).Trim());
        Assert.Equal("two", Workspace.Read(b.Stdout).Trim());
    }

    /// <summary>A registry whose deny list is fixed, for the tests that are about the rules.</summary>
    private sealed class DeniedWorkspace : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "terminalfs-tests-" + Guid.NewGuid().ToString("N"));

        internal DeniedWorkspace(params string[] rules)
        {
            Directory.CreateDirectory(root);

            string settings = Path.Combine(root, "settings.json");

            File.WriteAllText(
                settings,
                $$"""{ "permissions": { "deny": [{{string.Join(", ", rules.Select(r => $"\"{r}\""))}}] } }""");

            Watcher = SettingsWatcher.Start(settings, TimeSpan.FromSeconds(60));

            Registry = CommandRegistry.Create(new CommandOptions
            {
                OutputRoot = Path.Combine(root, "out"),
                WorkingDirectory = root,
                Settings = Watcher,
                Settle = TimeSpan.Zero,
            });
        }

        internal CommandRegistry Registry { get; }

        internal SettingsWatcher Watcher { get; }

        /// <summary>Takes a name and opens it, as a create followed by its open does.</summary>
        internal ControlSession Take(string id) =>
            Registry.OpenControl(Registry.CreateDraft(id), claiming: true);

        /// <summary>Reserves an id and starts it, taking the close-time path.</summary>
        internal Command Run(string id, string text)
        {
            Command command = Registry.Reserve(id);

            Registry.Start(command, text);

            return command;
        }

        public void Dispose()
        {
            Registry.Dispose();
            Watcher.Dispose();

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }
    }
}
