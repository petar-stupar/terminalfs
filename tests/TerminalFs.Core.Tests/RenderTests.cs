using System.Text;

namespace TerminalFs.Core.Tests;

/// <summary>
/// The pages this tree writes about itself. Every one carries OKF frontmatter whose <c>type</c>
/// says what it describes, and every link in one has to lead somewhere: a reader following a link
/// on a filesystem is doing a file read, and a broken one is an error rather than a missing page.
/// </summary>
public sealed class RenderTests : IDisposable
{
    private readonly Workspace workspace = new();

    public void Dispose() => workspace.Dispose();

    private CommandRegistry Registry => workspace.Registry;

    private static async Task<string> Text(TerminalNode node)
    {
        var page = (TerminalPage)node;

        return Encoding.UTF8.GetString(
            (await page.ContentAsync(TestContext.Current.CancellationToken)).Span);
    }

    private static TerminalDirectory SkillDirectory(Workspace workspace, string harness)
    {
        var skills = (TerminalDirectory)workspace.Registry.Root.Find("skills")!;
        var directory = (TerminalDirectory)skills.Find(harness)!;

        return (TerminalDirectory)directory.Find("terminalfs")!;
    }

    private static async Task<string> Skill(Workspace workspace, string harness) =>
        await Text(SkillDirectory(workspace, harness).Find("SKILL.md")!);

    private static IEnumerable<TerminalNode> Walk(TerminalDirectory directory)
    {
        foreach (TerminalNode child in directory.Children)
        {
            yield return child;

            if (child is TerminalDirectory nested)
            {
                foreach (TerminalNode below in Walk(nested))
                {
                    yield return below;
                }
            }
        }
    }

    [Fact]
    public async Task EveryPageOpensWithFrontmatterNamingItsType()
    {
        foreach (TerminalNode node in Walk(Registry.Root).Where(n => n is TerminalPage))
        {
            // The skill's frontmatter is the harness's, not OKF's: a harness matches on name and
            // description, and inventing extra fields there would be noise.
            if (string.Equals(node.Name, "SKILL.md", StringComparison.Ordinal))
            {
                continue;
            }

            string text = await Text(node);

            Assert.StartsWith("---\ntype: ", text, StringComparison.Ordinal);
            Assert.Contains("\n---\n", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EveryDirectoryCarriesAnIndex()
    {
        foreach (TerminalNode node in Walk(Registry.Root))
        {
            if (node is not TerminalDirectory directory)
            {
                continue;
            }

            Assert.True(
                directory.Find("index.md") is not null,
                $"'{directory.Key}' has no index.md for an agent descending into it");
        }

        Assert.NotNull(Registry.Root.Find("index.md"));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheRootNamesTheShellAndTheDirectoryCommandsRunIn()
    {
        string text = await Text(Registry.Root.Find("index.md")!);

        Assert.Contains(Registry.Shell.File, text, StringComparison.Ordinal);
        Assert.Contains(Registry.WorkingDirectory, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommandIndexSaysSoWhenNothingHasRun()
    {
        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;

        string text = await Text(commands.Find("index.md")!);

        Assert.Contains("Nothing has been run yet", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommandIndexListsEachCommandWithItsStatus()
    {
        Command first = workspace.Run("alpha", "echo one");
        Command second = workspace.Run("beta", "exit 3");

        await Workspace.Finished(first);
        await Workspace.Finished(second);

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        string text = await Text(commands.Find("index.md")!);

        Assert.Contains("| [alpha](alpha/status) | completed | 0 | `echo one` |", text, StringComparison.Ordinal);
        Assert.Contains("| [beta](beta/status) | error | 3 | `exit 3` |", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table row cannot carry a newline, and showing only the first line of a multi-line
    /// command would misrepresent what ran.
    /// </summary>
    [Fact]
    public async Task AMultiLineCommandIsMarkedAsContinuingInTheIndex()
    {
        workspace.Run("loop", "echo one\necho two");

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        string text = await Text(commands.Find("index.md")!);

        Assert.Contains("`echo one …`", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheControlIndexSaysHowToRunSomething()
    {
        var control = (TerminalDirectory)Registry.Root.Find("ctl")!;

        string text = await Text(control.Find("index.md")!);

        Assert.Contains("> /ctl/build", text, StringComparison.Ordinal);
        Assert.Contains("runs **once**", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Any name the tree can carry resolves, so a caller needs no create. A name already taken
    /// resolves too and is refused when it is opened: refusing it at the walk would answer "no
    /// such file", which is the opposite of the truth — the name is unavailable precisely because
    /// it exists.
    /// </summary>
    [Fact]
    public async Task OnlyANameSomebodyHasTakenIsAFileUnderCtl()
    {
        var control = (TerminalDirectory)Registry.Root.Find("ctl")!;

        // A name nobody has taken is no file, which is what leaves a client something to create.
        Assert.Null(control.Find("anything"));
        Assert.Null(control.Find(".hidden"));
        Assert.Equal(["index.md"], control.Children.Select(child => child.Name));

        using (ControlSession session = workspace.Take("t1"))
        {
            // One somebody has taken is a file, and is listed: a name nobody can see is a name
            // nobody can clean up.
            Assert.NotNull(control.Find("t1"));
            Assert.Equal(["index.md", "t1"], control.Children.Select(child => child.Name));
        }

        Command command = workspace.Run("t2", "echo hello");
        await Workspace.Finished(command);

        // A name that has run is not here either. It is a directory under /cmd, which is the
        // honest answer to where it went.
        Assert.Null(control.Find("t2"));

        CommandException refused = Assert.Throws<CommandException>(() => control.Create("t2"));

        Assert.Equal(CommandErrno.Exists, refused.Errno);
    }

    [Fact]
    public async Task AKillFileIsThereOnlyWhileTheCommandIsRunning()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var directory = (TerminalDirectory)commands.Find("t1")!;

        Assert.NotNull(directory.Find("kill"));

        ((TerminalKill)directory.Find("kill")!).Kill();
        await Workspace.Finished(command);

        Assert.Equal(CommandState.Error, command.State);
        Assert.Null(directory.Find("kill"));
    }

    /// <summary>
    /// A skill per harness, and nothing else: a harness pointed at <c>/skills</c> whole would
    /// otherwise find two skills with the same name and no way to tell which is its own.
    /// </summary>
    [Fact]
    public void EachHarnessHasASkillOfItsOwn()
    {
        var skills = (TerminalDirectory)Registry.Root.Find("skills")!;

        Assert.Equal(["index.md", "opencode", "claude-code"], skills.Children.Select(child => child.Name));

        foreach (string harness in new[] { "opencode", "claude-code" })
        {
            Assert.Equal(
                ["index.md", "SKILL.md"],
                SkillDirectory(workspace, harness).Children.Select(child => child.Name));
        }
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    public async Task EverySkillCarriesTheRulesTheHarnessesShare(string harness)
    {
        string text = await Skill(workspace, harness);

        Assert.StartsWith("---\nname: terminalfs\n", text, StringComparison.Ordinal);

        // One name per command, where a refusal's reason is, and not spending a turn on rm -r.
        Assert.Contains("A name runs once", text, StringComparison.Ordinal);
        Assert.Contains("`reason`", text, StringComparison.Ordinal);
        Assert.Contains("## Do not clean up", text, StringComparison.Ordinal);

        // The one thing an agent will otherwise waste a command discovering.
        Assert.Contains("no standard input", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>stdout</c> grows, so reading it before <c>wait</c> returns whatever had arrived by then
    /// and says nothing about it being partial.
    /// </summary>
    [Theory]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    public async Task EverySkillReadsWaitBeforeStdout(string harness)
    {
        string text = await Skill(workspace, harness);

        // The first example, not the prose around it, is what gets copied.
        int example = text.IndexOf("```", StringComparison.Ordinal);
        int wait = text.IndexOf("/wait", example, StringComparison.Ordinal);
        int stdout = text.IndexOf("/stdout", example, StringComparison.Ordinal);

        Assert.True(example >= 0 && wait > example, "the skill has no example that reads wait");
        Assert.True(stdout > wait, "the first example reads stdout before wait");
    }

    /// <summary>
    /// In the Bash shape a failed write does not stop the reads after it, and when the name was
    /// already used they print that command's output. Unless the skill says so, it reads as the
    /// answer to the command that was refused.
    /// </summary>
    [Fact]
    public async Task TheClaudeCodeSkillSaysAFailedWriteIsFollowedBySomebodyElsesOutput()
    {
        string text = await Skill(workspace, "claude-code");

        Assert.Contains("what the reads after it print is not your\ncommand's", text, StringComparison.Ordinal);
        Assert.Contains("The reads print that earlier command's state and output", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOpenCodeSkillWritesAndReadsInOneExecuteScript()
    {
        string text = await Skill(workspace, "opencode");

        Assert.Contains("inside a single `execute` script", text, StringComparison.Ordinal);
        Assert.Contains("await tools.file_write({ path: `${M}/ctl/${n}`", text, StringComparison.Ordinal);
        Assert.Contains("const M = \"<mount>\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shape is fixed so that a permission check can read the command out of the Bash call.
    /// Changing it breaks whatever parses it, so this pins it byte for byte.
    /// </summary>
    [Fact]
    public async Task TheClaudeCodeSkillWritesAndReadsInOneBashCallOfAFixedShape()
    {
        string text = await Skill(workspace, "claude-code");

        Assert.Contains(
            """
            ```sh
            cat > <mount>/ctl/build <<'CMD'
            dotnet build 2>&1 | tail -40
            CMD
            cat <mount>/cmd/build/wait; cat <mount>/cmd/build/stdout
            ```
            """,
            text,
            StringComparison.Ordinal);

        Assert.Contains("in the same Bash call", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The skills are the pages followed from outside the tree, so they are the ones that need to
    /// name where it is. Nobody said here, so they keep the placeholder: a path this server
    /// guessed at would send an agent to a directory that is not there, which is worse than one
    /// that asks to be filled in. Each says where the real one comes from instead.
    /// </summary>
    [Fact]
    public async Task EachSkillSaysWhereItsPathComesFromWhenNobodyHasSaid()
    {
        string openCode = await Skill(workspace, "opencode");

        Assert.Contains("<mount>/ctl/<name>", openCode, StringComparison.Ordinal);
        Assert.Contains(
            "the path you read this skill from,\nwithout `/skills/opencode/terminalfs/SKILL.md` on the end",
            openCode,
            StringComparison.Ordinal);

        Assert.Contains(
            "how to work out the",
            await Text(SkillDirectory(workspace, "opencode").Find("index.md")!),
            StringComparison.Ordinal);

        string claudeCode = await Skill(workspace, "claude-code");

        Assert.Contains("<mount>/ctl/build", claudeCode, StringComparison.Ordinal);
        Assert.Contains("Your session context names it", claudeCode, StringComparison.Ordinal);

        Assert.Contains(
            "Replace that with where this tree is mounted",
            await Text(SkillDirectory(workspace, "claude-code").Find("index.md")!),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    public async Task EverySkillNamesTheMountpointWhenOneIsKnown(string harness)
    {
        using var mounted = new Workspace(new CommandOptions { MountPath = "/mnt/tfs" });

        string text = await Skill(mounted, harness);

        Assert.Contains("The tree is mounted at `/mnt/tfs`.", text, StringComparison.Ordinal);
        Assert.Contains("/mnt/tfs/ctl/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<mount>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<where>", text, StringComparison.Ordinal);

        Assert.Contains(
            "already the ones on this machine",
            await Text(SkillDirectory(mounted, harness).Find("index.md")!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A Claude Code skill is a copy taken out of one tree and kept, and a tree per session means
    /// the next session's is somewhere else. So a copy naming a path still gives way to the one the
    /// session context names.
    /// </summary>
    [Fact]
    public async Task TheClaudeCodeSkillGivesWayToTheMountTheSessionNames()
    {
        using var mounted = new Workspace(new CommandOptions { MountPath = "/mnt/tfs" });

        Assert.Contains(
            "If your session context names a terminalfs\nmount of its own, that one is yours",
            await Skill(mounted, "claude-code"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Only <c>&lt;mount&gt;</c> is substituted. The other angle-bracketed words are placeholders
    /// a reader is meant to fill in themselves, and a general template pass would eat them.
    /// </summary>
    [Theory]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    public async Task EverySkillLeavesItsOtherPlaceholdersAlone(string harness)
    {
        using var mounted = new Workspace(new CommandOptions { MountPath = "/mnt/tfs" });

        Assert.Contains("/mnt/tfs/ctl/<name>", await Skill(mounted, harness), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("claude-code")]
    public async Task ATrailingSeparatorOnTheMountpointDoesNotDoubleUp(string harness)
    {
        using var mounted = new Workspace(new CommandOptions { MountPath = "/mnt/tfs/" });

        string text = await Skill(mounted, harness);

        Assert.Contains("/mnt/tfs/ctl/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("//ctl", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeniedCommandDirectoryHoldsOnlyWhatWasAskedWhatHappenedAndWhy()
    {
        // Refused for its shape rather than by a rule, so this needs no settings file.
        // Whitespace and not nothing: a close with no bytes in it leaves the name a draft.
        workspace.Write("t1", " ");

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var directory = (TerminalDirectory)commands.Find("t1")!;

        Assert.Equal(
            ["command", "status", "reason"],
            directory.Children.Select(child => child.Name));

        Assert.Equal("denied\n", await Text(directory.Find("status")!));
        Assert.Contains("no command", await Text(directory.Find("reason")!), StringComparison.Ordinal);
        Assert.EndsWith("\n", await Text(directory.Find("reason")!), StringComparison.Ordinal);

        foreach (string absent in new[] { "pid", "exitcode", "stdout", "stderr", "wait", "kill" })
        {
            Assert.Null(directory.Find(absent));
        }
    }

    [Fact]
    public async Task ADirectoryListsPidWhileRunningAndExitCodeAfter()
    {
        Command command = workspace.Run("t1", Workspace.Sleep(30));

        while (command.Pid is null && !command.HasExited)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var directory = (TerminalDirectory)commands.Find("t1")!;

        Assert.NotNull(directory.Find("pid"));
        Assert.Null(directory.Find("exitcode"));

        command.Kill();
        await Workspace.Finished(command);

        Assert.Null(directory.Find("pid"));
        Assert.NotNull(directory.Find("exitcode"));
    }

    /// <summary>
    /// The revision is the only field that can tell a caching client that what is at a path has
    /// changed, since the path itself is still the same place.
    /// </summary>
    [Fact]
    public async Task AStatusRevisionMovesWhenTheCommandDoes()
    {
        // A command that cannot finish until it is told to: one that finished on its own could
        // do so before the revision was first read, and on a fast machine an echo did.
        Command command = workspace.Run("t1", "sleep 300");

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var directory = (TerminalDirectory)commands.Find("t1")!;

        uint before = directory.Find("status")!.Revision;

        command.Kill();
        await Workspace.Finished(command);

        Assert.True(
            directory.Find("status")!.Revision > before,
            "the status revision did not move when the command finished");
    }

    [Fact]
    public async Task OnlyTheCommandDirectoriesAreWritable()
    {
        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var skills = (TerminalDirectory)Registry.Root.Find("skills")!;

        Assert.False(Registry.Root.Writable);
        Assert.False(skills.Writable);
        Assert.True(commands.Writable);

        Command command = workspace.Run("t1", "echo hello");
        await Workspace.Finished(command);

        Assert.True(((TerminalDirectory)commands.Find("t1")!).Writable);
    }

    [Fact]
    public void RemovingSomethingFromAReadOnlyDirectoryIsRefused()
    {
        var skills = (TerminalDirectory)Registry.Root.Find("skills")!;

        CommandException refused = Assert.Throws<CommandException>(() => skills.Remove("index.md", false));

        Assert.Equal(CommandErrno.ReadOnly, refused.Errno);
    }

    [Fact]
    public async Task UnlinkingAFieldTakesItOutOfTheListing()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Workspace.Finished(command);

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var directory = (TerminalDirectory)commands.Find("t1")!;

        directory.Remove("stdout", directory: false);

        Assert.Null(directory.Find("stdout"));
        Assert.DoesNotContain(directory.Children, child => child.Name == "stdout");
    }

    [Fact]
    public async Task RemovingACommandDirectoryTakesItOutOfCmd()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Workspace.Finished(command);

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;

        commands.Remove("t1", directory: true);

        Assert.Null(commands.Find("t1"));
    }

    [Fact]
    public void TheIndexOfCmdIsNotACommand()
    {
        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;

        Assert.Throws<CommandException>(() => commands.Remove("index.md", directory: true));
    }

    [Fact]
    public void EveryKeyInTheTreeIsDistinct()
    {
        string[] keys = [.. Walk(Registry.Root).Select(node => node.Key)];

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A page that can change must never state a size larger than the read will produce: a client
    /// stops at the first short read and calls that the end of the file.
    /// </summary>
    [Fact]
    public async Task AStatedSizeIsWhatTheReadProduces()
    {
        Command command = workspace.Run("t1", "echo hello");
        await Workspace.Finished(command);

        foreach (TerminalNode node in Walk(Registry.Root).Where(n => n is TerminalPage))
        {
            var page = (TerminalPage)node;

            ulong stated = await page.SizeAsync(TestContext.Current.CancellationToken);
            int read = (await page.ContentAsync(TestContext.Current.CancellationToken)).Length;

            Assert.Equal((ulong)read, stated);
        }
    }
}
