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

    private static async Task<string> Skill(Workspace workspace)
    {
        var skills = (TerminalDirectory)workspace.Registry.Root.Find("skills")!;
        var skill = (TerminalDirectory)skills.Find("terminalfs")!;

        return await Text(skill.Find("SKILL.md")!);
    }

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
    public async Task AnyValidNameResolvesUnderCtlAndATakenOneIsRefusedAtTheOpen()
    {
        var control = (TerminalDirectory)Registry.Root.Find("ctl")!;

        Assert.NotNull(control.Find("anything"));
        Assert.Null(control.Find(".hidden"));

        Command command = workspace.Run("t1", "echo hello");
        await Workspace.Finished(command);

        var taken = (TerminalControl)control.Find("t1")!;

        CommandException refused = Assert.Throws<CommandException>(() => taken.Open());

        Assert.Equal(CommandErrno.Exists, refused.Errno);

        // The listing itself holds only the index: a name here is a command nobody has written
        // yet, and there is no list of those.
        Assert.Equal(["index.md"], control.Children.Select(child => child.Name));
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

    [Fact]
    public async Task TheSkillDescribesRunningWaitingReadingAndRemoving()
    {
        string text = await Skill(workspace);

        Assert.StartsWith("---\nname: terminalfs\n", text, StringComparison.Ordinal);

        foreach (string mentioned in new[] { "> <mount>/ctl", "/wait", "/exitcode", "rm -r", "kill " })
        {
            Assert.Contains(mentioned, text, StringComparison.Ordinal);
        }

        // The one thing an agent will otherwise waste a command discovering.
        Assert.Contains("no standard input", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The skill is the one page meant to be copied out of the tree and followed from outside it,
    /// so it is the one page that needs to name where the tree is. Nobody said here, so it keeps
    /// the placeholder: a path this server guessed at would send an agent to a directory that is
    /// not there, which is worse than one that asks to be filled in.
    /// </summary>
    [Fact]
    public async Task TheSkillKeepsItsPlaceholderWhenNobodyHasSaidWhereTheTreeIs()
    {
        Assert.Contains("<mount>/ctl/build", await Skill(workspace), StringComparison.Ordinal);

        var skills = (TerminalDirectory)Registry.Root.Find("skills")!;
        var skill = (TerminalDirectory)skills.Find("terminalfs")!;

        Assert.Contains(
            "replace that with where this tree is",
            await Text(skill.Find("index.md")!),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSkillNamesTheMountpointWhenOneIsKnown()
    {
        using var mounted = new Workspace(new CommandOptions { MountPath = "/mnt/tfs" });

        string text = await Skill(mounted);

        Assert.Contains("echo 'dotnet build' > /mnt/tfs/ctl/build", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<mount>", text, StringComparison.Ordinal);

        var skills = (TerminalDirectory)mounted.Registry.Root.Find("skills")!;
        var skill = (TerminalDirectory)skills.Find("terminalfs")!;

        Assert.Contains(
            "copy it as it is",
            await Text(skill.Find("index.md")!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Only <c>&lt;mount&gt;</c> is substituted. The other angle-bracketed words are placeholders
    /// a reader is meant to fill in themselves, and a general template pass would eat them.
    /// </summary>
    [Fact]
    public async Task TheSkillLeavesItsOtherPlaceholdersAlone()
    {
        using var mounted = new Workspace(new CommandOptions { MountPath = "/mnt/tfs" });

        Assert.Contains("/mnt/tfs/ctl/<name>", await Skill(mounted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrailingSeparatorOnTheMountpointDoesNotDoubleUp()
    {
        using var mounted = new Workspace(new CommandOptions { MountPath = "/mnt/tfs/" });

        Assert.Contains("/mnt/tfs/ctl/build", await Skill(mounted), StringComparison.Ordinal);
        Assert.DoesNotContain("//ctl", await Skill(mounted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeniedCommandDirectoryHoldsOnlyWhatWasAskedWhatHappenedAndWhy()
    {
        // Refused for its shape rather than by a rule, so this needs no settings file.
        Registry.OpenControl("t1").Close();

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var directory = (TerminalDirectory)commands.Find("t1")!;

        Assert.Equal(
            ["command", "status", "reason"],
            directory.Children.Select(child => child.Name));

        Assert.Equal("denied\n", await Text(directory.Find("status")!));
        Assert.Contains("no command text", await Text(directory.Find("reason")!), StringComparison.Ordinal);
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
        Command command = workspace.Run("t1", "echo hello");

        var commands = (TerminalDirectory)Registry.Root.Find("cmd")!;
        var directory = (TerminalDirectory)commands.Find("t1")!;

        uint before = directory.Find("status")!.Revision;

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
