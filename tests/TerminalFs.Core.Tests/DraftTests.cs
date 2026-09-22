using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace TerminalFs.Core.Tests;

/// <summary>
/// A name under <c>/ctl</c> before it is a command: taken, written to, renamed, removed, and
/// finally decided. Nothing appears under <c>/cmd</c> until it is, which is what keeps a name
/// nobody wrote a command for from leaving anything behind.
/// </summary>
public sealed class DraftTests : IDisposable
{
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    private readonly FakeTimeProvider time = new();
    private readonly Workspace workspace;

    public DraftTests() =>
        workspace = new Workspace(
            new CommandOptions { KeepAfterExit = Keep, TimeProvider = time },
            settle: Settle);

    public void Dispose() => workspace.Dispose();

    private CommandRegistry Registry => workspace.Registry;

    private TerminalDirectory Commands => (TerminalDirectory)Registry.Root.Find("cmd")!;

    private TerminalDirectory Control => (TerminalDirectory)Registry.Root.Find("ctl")!;

    private static void Write(ControlSession session, string text) =>
        session.Write(Encoding.UTF8.GetBytes(text));

    /// <summary>Writes a command to a name and closes it, without settling.</summary>
    private void Written(string id, string text)
    {
        using ControlSession session = workspace.Take(id);

        Write(session, text);
    }

    private static async Task<string> Read(TerminalNode node) =>
        Encoding.UTF8.GetString(
            (await ((TerminalPage)node).ContentAsync(TestContext.Current.CancellationToken)).Span);

    // ---- taking a name ----------------------------------------------------

    /// <summary>
    /// The name is held and nothing else. A directory the moment a name was taken is what filled
    /// <c>/cmd</c> with probe opens, temporary files and names nobody ever ran anything under.
    /// </summary>
    [Fact]
    public void TakingANameMakesNoDirectoryAndNoOutput()
    {
        using ControlSession session = workspace.Take("t1");

        Assert.NotNull(Registry.FindDraft("t1"));
        Assert.Null(Registry.Find("t1"));
        Assert.Null(Commands.Find("t1"));
        Assert.False(Directory.Exists(Path.Combine(Registry.OutputRoot, "t1")));
    }

    [Fact]
    public void ASecondOpenWhileASessionIsOpenOnTheDraftIsRefused()
    {
        using ControlSession first = workspace.Take("t1");

        CommandException refused = Assert.Throws<CommandException>(
            () => Registry.OpenControl(Registry.FindDraft("t1")!, claiming: false));

        Assert.Equal(CommandErrno.Exists, refused.Errno);
    }

    /// <summary>
    /// A name that has been written to is spoken for, even before it has run. Letting a second
    /// writer in would mean two commands under one name, and only one of them could run.
    /// </summary>
    [Fact]
    public void ASecondOpenOfADraftThatCarriesBytesIsRefused()
    {
        Written("t1", "echo hello");

        CommandException refused = Assert.Throws<CommandException>(
            () => Registry.OpenControl(Registry.FindDraft("t1")!, claiming: false));

        Assert.Equal(CommandErrno.Exists, refused.Errno);
    }

    /// <summary>
    /// The open a create is entitled to cannot be taken by anyone else. A create and its open are
    /// two messages, and without this another caller could open a name in between and win one
    /// they never made.
    /// </summary>
    [Fact]
    public void OnlyTheCallerThatMadeANameGetsItsFirstOpen()
    {
        Draft draft = Registry.CreateDraft("t1");

        CommandException refused = Assert.Throws<CommandException>(
            () => Registry.OpenControl(draft, claiming: false));

        Assert.Equal(CommandErrno.Exists, refused.Errno);

        using ControlSession creator = Registry.OpenControl(draft, claiming: true);

        Assert.Equal("t1", creator.Draft.Name);
    }

    // ---- the keep clock ---------------------------------------------------

    [Fact]
    public void ADraftNobodyWroteToIsFreedOnTheKeepClock()
    {
        workspace.Take("t1").Close();

        Assert.NotNull(Registry.FindDraft("t1"));

        time.Advance(Keep);

        Assert.Null(Registry.FindDraft("t1"));
        Assert.Null(Registry.Find("t1"));
    }

    /// <summary>
    /// A create whose open never arrives — a client that died between the two — would otherwise
    /// hold the name for the life of the server.
    /// </summary>
    [Fact]
    public void ADraftMadeAndNeverOpenedIsFreedOnTheKeepClock()
    {
        Registry.CreateDraft("t1");

        time.Advance(Keep);

        Assert.Null(Registry.FindDraft("t1"));
    }

    [Fact]
    public void OpeningADraftStopsItsKeepClock()
    {
        workspace.Take("t1").Close();

        using ControlSession again = Registry.OpenControl(Registry.FindDraft("t1")!, claiming: false);

        time.Advance(Keep * 3);

        Assert.NotNull(Registry.FindDraft("t1"));
    }

    [Fact]
    public void AFreedNameCanBeTakenAgain()
    {
        workspace.Take("t1").Close();
        time.Advance(Keep);

        using ControlSession again = workspace.Take("t1");

        Assert.NotNull(Registry.FindDraft("t1"));
    }

    [Fact]
    public void RemovingADraftFreesItsNameAtOnce()
    {
        workspace.Take("t1").Close();

        Control.Remove("t1", directory: false);

        Assert.Null(Registry.FindDraft("t1"));

        using ControlSession again = workspace.Take("t1");

        Assert.NotNull(Registry.FindDraft("t1"));
    }

    // ---- settling ---------------------------------------------------------

    /// <summary>
    /// The close does not spawn. A client that writes to a temporary name and renames it into
    /// place closes the temporary file before it renames, so running on the close would run the
    /// command under a name nobody chose.
    /// </summary>
    [Fact]
    public void ACloseCarryingBytesDoesNotRunUntilTheDraftSettles()
    {
        Written("t1", "echo hello");

        Assert.True(Registry.FindDraft("t1")!.Decided);
        Assert.Null(Registry.Find("t1"));

        time.Advance(Settle);

        Assert.NotNull(Registry.Find("t1"));
        Assert.Null(Registry.FindDraft("t1"));
    }

    /// <summary>
    /// The sharp edge, and the reason the clock is a backstop rather than the mechanism:
    /// <c>echo … &gt; ctl/t1; cat cmd/t1/wait</c> is one shell line, and the walk to <c>t1</c>
    /// must not miss a command whose bytes have already been written. No clock moves here.
    /// </summary>
    [Fact]
    public void ALookupOfThatNameUnderCmdRunsTheDraftAtOnce()
    {
        Written("t1", "echo hello");

        Assert.NotNull(Commands.Find("t1"));
        Assert.NotNull(Registry.Find("t1"));
    }

    /// <summary>
    /// And only that name. Settling every draft on any lookup would mean one caller reading
    /// <c>cmd/other/stdout</c> spawning somebody else's half-written temporary file.
    /// </summary>
    [Fact]
    public void ALookupOfAnotherNameUnderCmdLeavesADraftAlone()
    {
        Written("t1", "echo hello");

        Assert.Null(Commands.Find("t2"));

        Assert.NotNull(Registry.FindDraft("t1"));
        Assert.Null(Registry.Find("t1"));
    }

    /// <summary>
    /// A listing that left out a name a walk would then find is a listing that contradicts itself
    /// one message later.
    /// </summary>
    [Fact]
    public void AListingOfCmdRunsEveryDraftThatHasBeenDecided()
    {
        Written("t1", "echo one");
        Written("t2", "echo two");

        Assert.Contains("t1", Commands.Children.Select(child => child.Name));
        Assert.Contains("t2", Commands.Children.Select(child => child.Name));
    }

    /// <summary>
    /// The index names every command and links to each one's status, so a link that led nowhere
    /// would be worse than a page that took a moment to render.
    /// </summary>
    [Fact]
    public async Task TheCommandIndexRunsEveryDraftThatHasBeenDecided()
    {
        Written("t1", "echo hello");

        Assert.Contains("t1", await Read(Commands.Find("index.md")!), StringComparison.Ordinal);
        Assert.NotNull(Registry.Find("t1"));
    }

    /// <summary>
    /// Only an operation that asks about <c>/cmd</c> decides a draft. <c>/ctl</c> is where a name
    /// is still being decided, so nothing there decides it — a listing of the names in flight
    /// least of all.
    /// </summary>
    [Fact]
    public void NothingUnderCtlRunsADraft()
    {
        Written("t1", "echo hello");

        Assert.NotNull(Control.Find("t1"));
        Assert.Equal(["index.md", "t1"], Control.Children.Select(child => child.Name));

        Assert.Null(Registry.Find("t1"));
    }

    /// <summary>
    /// The root page counts commands; it names none. It is often the first thing an agent reads,
    /// and reading it must not decide every name in flight.
    /// </summary>
    [Fact]
    public async Task TheRootIndexCountsCommandsWithoutRunningADraft()
    {
        Written("t1", "echo hello");

        await Read(Registry.Root.Find("index.md")!);

        Assert.Null(Registry.Find("t1"));
    }

    [Fact]
    public void ADraftWithNoBytesNeverRuns()
    {
        workspace.Take("t1").Close();

        Assert.Null(Commands.Find("t1"));

        time.Advance(Settle);

        Assert.Null(Registry.Find("t1"));
        Assert.NotNull(Registry.FindDraft("t1"));
    }

    /// <summary>
    /// A settle timer and a lookup can arrive together, and only one of them may leave with the
    /// bytes.
    /// </summary>
    [Fact]
    public void ADraftRunsOnlyOnce()
    {
        Written("t1", "echo hello");

        // The lookup commits it; the timer that would have is gone with the verdict it claimed.
        Assert.NotNull(Commands.Find("t1"));

        Command first = Registry.Find("t1")!;

        time.Advance(Settle * 4);
        Registry.Settle("t1");

        Assert.Same(first, Registry.Find("t1"));
    }

    // ---- rename -----------------------------------------------------------

    /// <summary>
    /// The shape this whole design exists for: a client writes to a temporary name, closes it,
    /// and renames it into place. The command runs under the name it meant, and no command ever
    /// existed under the temporary one.
    /// </summary>
    [Fact]
    public void RenamingADraftMovesItAndNothingEverRunsUnderTheTemporaryName()
    {
        Written("w1.tmp.87694", "echo hello");

        Control.Rename("w1.tmp.87694", Control, "w1");

        Assert.Null(Registry.FindDraft("w1.tmp.87694"));
        Assert.NotNull(Registry.FindDraft("w1"));

        time.Advance(Settle);

        Assert.NotNull(Registry.Find("w1"));
        Assert.Equal("echo hello", Registry.Find("w1")!.Text);

        Assert.Null(Registry.Find("w1.tmp.87694"));
        Assert.False(Directory.Exists(Path.Combine(Registry.OutputRoot, "w1.tmp.87694")));
    }

    /// <summary>
    /// A rename moves the name and not the file, so the qid the 9P layer derives from a draft's
    /// key must not move with it: the server matches on that qid when it rebases a fid's ancestry
    /// after the rename, and one that moved would match nothing.
    /// </summary>
    [Fact]
    public void ARenamedDraftIsStillTheSameFile()
    {
        using ControlSession session = workspace.Take("t1");

        string before = Control.Find("t1")!.Key;

        Control.Rename("t1", Control, "t2");

        Assert.Equal(before, Control.Find("t2")!.Key);
    }

    /// <summary>
    /// POSIX rename replaces what is at the destination. Here what is there is either a draft
    /// somebody else is writing or a command's whole output, and destroying either silently is
    /// the one thing this tree does not do.
    /// </summary>
    [Fact]
    public void RenamingOntoANameThatIsTakenIsRefused()
    {
        using ControlSession first = workspace.Take("t1");
        using ControlSession second = workspace.Take("t2");

        CommandException refused = Assert.Throws<CommandException>(
            () => Control.Rename("t1", Control, "t2"));

        Assert.Equal(CommandErrno.Exists, refused.Errno);
    }

    [Fact]
    public void RenamingADraftOutOfCtlIsRefused()
    {
        using ControlSession session = workspace.Take("t1");

        CommandException refused = Assert.Throws<CommandException>(
            () => Control.Rename("t1", Commands, "t1"));

        Assert.Equal(CommandErrno.CrossDevice, refused.Errno);
    }

    /// <summary>
    /// A command's name is fixed once it has run: the name is its output directory on disk and
    /// names every file under it.
    /// </summary>
    [Fact]
    public void RenamingANameThatHasAlreadyRunIsRefused()
    {
        Written("t1", "echo hello");
        Assert.NotNull(Commands.Find("t1"));

        CommandException refused = Assert.Throws<CommandException>(
            () => Control.Rename("t1", Control, "t2"));

        Assert.Equal(CommandErrno.NotPermitted, refused.Errno);
    }

    /// <summary>
    /// A caller who renames a decided draft is finished with it, so pushing the deadline back
    /// would only delay a command that has already been decided.
    /// </summary>
    [Fact]
    public void RenamingADraftDoesNotRestartItsSettleClock()
    {
        Written("t1", "echo hello");

        time.Advance(Settle / 2);
        Control.Rename("t1", Control, "t2");
        time.Advance(Settle / 2);

        Assert.NotNull(Registry.Find("t2"));
    }

    // ---- refusals ---------------------------------------------------------

    /// <summary>
    /// A refusal is final the moment it is given, so a refused draft settles like any other — and
    /// a caller who writes to a temporary name finds the reason under the name they meant.
    /// </summary>
    [Fact]
    public void ARenamedRefusedDraftCarriesItsRefusalToTheFinalName()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)", time, Settle);

        var control = (TerminalDirectory)denied.Registry.Root.Find("ctl")!;

        using (ControlSession session = denied.Take("x.tmp"))
        {
            Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        }

        control.Rename("x.tmp", control, "x");
        time.Advance(Settle);

        Command command = denied.Registry.Find("x")!;

        Assert.Equal(CommandState.Denied, command.State);
        Assert.Contains("Bash(sudo:*)", command.Reason!, StringComparison.Ordinal);
        Assert.Null(denied.Registry.Find("x.tmp"));
    }

    /// <summary>
    /// 0.2.0's promise, through the settle window: a caller whose write failed looks under
    /// <c>/cmd/&lt;name&gt;</c> at once, and the reason is there.
    /// </summary>
    [Fact]
    public void ALookupOfARefusedNameUnderCmdFindsItsReasonAtOnce()
    {
        using var denied = new DeniedWorkspace("Bash(sudo:*)", time, Settle);

        using (ControlSession session = denied.Take("t1"))
        {
            Assert.Throws<CommandException>(() => Write(session, "sudo ls"));
        }

        var commands = (TerminalDirectory)denied.Registry.Root.Find("cmd")!;

        Assert.NotNull(commands.Find("t1"));
        Assert.Contains("Bash(sudo:*)", denied.Registry.Find("t1")!.Reason!, StringComparison.Ordinal);
    }

    // ---- shutdown and identity -------------------------------------------

    /// <summary>
    /// A decided draft at shutdown is a command that never runs although its write and its close
    /// both succeeded. That is uncomfortable and still right: the alternative is starting a
    /// process during shutdown and killing it in the next breath.
    /// </summary>
    [Fact]
    public void DisposingTheRegistryDropsEveryDraftWithoutRunningIt()
    {
        using var closing = new Workspace(new CommandOptions { TimeProvider = time }, settle: Settle);

        using (ControlSession session = closing.Take("t1"))
        {
            Write(session, "echo hello");
        }

        Draft draft = closing.Registry.FindDraft("t1")!;

        closing.Registry.Dispose();

        Assert.True(draft.Gone);
    }

    /// <summary>
    /// A name reused after its command was removed is a different file, and must not inherit the
    /// old one's qid: a client caching on it would serve the removed command's output for the new
    /// one.
    /// </summary>
    [Fact]
    public async Task AReusedNameIsADifferentFileWithADifferentKey()
    {
        Command first = workspace.Run("t1", "echo one");
        await Workspace.Finished(first);

        string before = Commands.Find("t1")!.Key;

        Registry.RemoveTree("t1");

        Command second = workspace.Run("t1", "echo two");
        await Workspace.Finished(second);

        Assert.NotEqual(before, Commands.Find("t1")!.Key);
    }

    /// <summary>A registry whose rules refuse something, on a clock a test can move.</summary>
    private sealed class DeniedWorkspace : IDisposable
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "terminalfs-tests-" + Guid.NewGuid().ToString("N"));

        internal DeniedWorkspace(string rule, TimeProvider time, TimeSpan settle)
        {
            Directory.CreateDirectory(root);

            string settings = Path.Combine(root, "settings.json");

            File.WriteAllText(settings, $$"""{ "permissions": { "deny": ["{{rule}}"] } }""");

            Watcher = Permissions.SettingsWatcher.Start(settings, TimeSpan.FromSeconds(60));

            Registry = CommandRegistry.Create(new CommandOptions
            {
                OutputRoot = Path.Combine(root, "out"),
                WorkingDirectory = root,
                Settings = Watcher,
                TimeProvider = time,
                Settle = settle,
            });
        }

        internal CommandRegistry Registry { get; }

        private Permissions.SettingsWatcher Watcher { get; }

        internal ControlSession Take(string id) =>
            Registry.OpenControl(Registry.CreateDraft(id), claiming: true);

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
