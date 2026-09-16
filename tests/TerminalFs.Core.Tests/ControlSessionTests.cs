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
    /// Opening the file is what takes the name, so two callers racing for one resolve at the
    /// moment they collide rather than after both have written.
    /// </summary>
    [Fact]
    public void TheNameIsTakenWhenTheFileIsOpened()
    {
        using ControlSession session = Registry.OpenControl("t1");

        Assert.NotNull(Registry.Find("t1"));
        Assert.Equal(CommandState.Reserved, Registry.Find("t1")!.State);
    }

    [Fact]
    public void AnInvalidNameIsRefusedAtTheOpen()
    {
        CommandException refused = Assert.Throws<CommandException>(() => Registry.OpenControl("a/b"));

        Assert.Equal(CommandErrno.InvalidArgument, refused.Errno);
    }

    /// <summary>
    /// Nothing runs until the file is closed, because until then the command is not finished
    /// being written: the next write could add another line to it.
    /// </summary>
    [Fact]
    public void NothingRunsBeforeClose()
    {
        using ControlSession session = Registry.OpenControl("t1");

        Write(session, "echo hello");

        Assert.Equal(CommandState.Reserved, Registry.Find("t1")!.State);
        Assert.Null(Registry.Find("t1")!.Pid);
    }

    [Fact]
    public async Task ClosingRunsWhatWasWritten()
    {
        ControlSession session = Registry.OpenControl("t1");
        Write(session, "echo hello");
        session.Close();

        Command command = Registry.Find("t1")!;

        Assert.Equal(CommandState.Completed, await Finished(command));
        Assert.Equal("hello", Workspace.Read(command.Stdout).Trim());
    }

    [Fact]
    public void ADuplicateNameIsRefusedWithExists()
    {
        using ControlSession first = Registry.OpenControl("t1");

        CommandException refused = Assert.Throws<CommandException>(() => Registry.OpenControl("t1"));

        Assert.Equal(CommandErrno.Exists, refused.Errno);
    }

    [Fact]
    public async Task ABodySplitAcrossWritesIsJoined()
    {
        ControlSession session = Registry.OpenControl("t1");

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
        ControlSession session = Registry.OpenControl("t1");

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
        using ControlSession session = small.Registry.OpenControl("t1");

        CommandException refused = Assert.Throws<CommandException>(
            () => Write(session, new string('x', 200)));

        Assert.Equal(CommandErrno.TooLarge, refused.Errno);
    }

    [Fact]
    public async Task CloseWithoutABodyMarksTheCommandFailed()
    {
        ControlSession session = Registry.OpenControl("t1");
        session.Close();

        Command command = Registry.Find("t1")!;
        await Finished(command);

        Assert.Equal(CommandState.Error, command.State);
        Assert.Contains("no command text", Workspace.Read(command.Stderr), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeniedCommandFailsTheWriteNamingTheRule()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");
        using ControlSession session = denied.Registry.OpenControl("t1");

        CommandException refused = Assert.Throws<CommandException>(() => Write(session, "sudo ls"));

        Assert.Equal(CommandErrno.NotPermitted, refused.Errno);
        Assert.Contains("Bash(sudo:*)", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The name is given back, because nothing ran under it.</summary>
    [Fact]
    public void ADeniedCommandLeavesItsNameFree()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");

        using (ControlSession session = denied.Registry.OpenControl("t1"))
        {
            Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        }

        Assert.Null(denied.Registry.Find("t1"));

        using ControlSession again = denied.Registry.OpenControl("t1");

        Assert.NotNull(denied.Registry.Find("t1"));
    }

    /// <summary>
    /// A command can only reach a denied shape at the end: the first write says <c>cd /tmp</c>
    /// and the second adds <c>&amp;&amp; sudo ls</c>.
    /// </summary>
    [Fact]
    public void ADenialSeenOnlyAtTheSecondWriteStillStopsIt()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");

        using (ControlSession session = denied.Registry.OpenControl("t1"))
        {
            Write(session, "cd /tmp");

            Assert.Throws<CommandException>(() => Write(session, " && sudo ls"));
        }

        Assert.Null(denied.Registry.Find("t1"));
    }

    [Fact]
    public void AMentionOfADeniedWordIsNotADeniedCommand()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");
        using ControlSession session = denied.Registry.OpenControl("t1");

        Write(session, "echo sudo is only text");

        Assert.NotNull(denied.Registry.Find("t1"));
    }

    /// <summary>
    /// A mount retries a write that failed. Answering the second attempt with something about the
    /// state this session is now in would describe the machinery rather than the mistake, and
    /// would put an entry in the refusal log that nobody caused.
    /// </summary>
    [Fact]
    public void ARetriedWriteIsRefusedWithTheSameReason()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)");
        using ControlSession session = denied.Registry.OpenControl("t1");

        CommandException first = Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        CommandException again = Assert.Throws<CommandException>(() => Write(session, "sudo ls"));

        Assert.Equal(first.Message, again.Message);
        Assert.Single(denied.Registry.Refusals);
    }

    /// <summary>
    /// Two callers writing at once. Each has its own file and its own session, which is the whole
    /// reason the name is the path rather than a word inside one file.
    /// </summary>
    [Fact]
    public async Task TwoOpensAtOnceRunTwoCommandsIndependently()
    {
        ControlSession first = Registry.OpenControl("p1");
        ControlSession second = Registry.OpenControl("p2");

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
            });
        }

        internal CommandRegistry Registry { get; }

        internal SettingsWatcher Watcher { get; }

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
