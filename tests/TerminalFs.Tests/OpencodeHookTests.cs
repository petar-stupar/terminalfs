using System.Text.Json;
using TerminalFs.Internal.Hooks;
using TerminalFs.Internal.Sessions;

namespace TerminalFs.Tests;

/// <summary>
/// What the opencode plugin is told to do with one of opencode's permission checks: a write of a
/// command to the session's tree is held to the session's shell rules, and nothing else reaches the
/// tree.
/// </summary>
public sealed class OpencodeHookTests
{
    private const string Session = "ses_2a9f0c1d";

    private readonly SessionPaths paths = new("/run/user/1000/terminalfs");

    public OpencodeHookTests() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "session trees and the commands that name them are POSIX");

    private string Tree => paths.MountPath(Session);

    private OpencodeDecision Check(string action, string? tool, object? input, object[]? rules = null, string[]? resources = null) =>
        new OpencodeHook(paths, _ => null, "/home/agent").Check(OpencodeCheck.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = Session,
            ["directory"] = "/home/agent/project",
            ["action"] = action,
            ["resources"] = resources ?? [],
            ["tool"] = tool,
            ["input"] = input,
            ["rules"] = rules ?? [new { action = "*", resource = "*", effect = "allow" }],
        })));

    private static object Rule(string resource, string effect) => new { action = "shell", resource, effect };

    [Fact]
    public void AWrittenCommandIsHeldToTheShellRules()
    {
        object[] rules = [Rule("*", "ask"), Rule("dotnet *", "allow"), Rule("sudo *", "deny")];

        Assert.Equal("allow", Check("edit", "write", new { path = $"{Tree}/ctl/build", content = "dotnet build" }, rules).Effect);
        Assert.Equal("ask", Check("edit", "write", new { path = $"{Tree}/ctl/make", content = "make" }, rules).Effect);

        OpencodeDecision denied = Check("edit", "write", new { path = $"{Tree}/ctl/root", content = "sudo ls" }, rules);

        Assert.Equal("deny", denied.Effect);
        Assert.Contains("'sudo *'", denied.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// opencode's own defaults allow everything, so a session with no shell rules runs what it is
    /// told, exactly as it would with its shell tool.
    /// </summary>
    [Fact]
    public void WithOpencodesDefaultsACommandRuns() =>
        Assert.Equal("allow", Check("edit", "write", new { path = $"{Tree}/ctl/x", content = "make" }).Effect);

    [Fact]
    public void AnotherSessionsTreeIsRefused()
    {
        string other = paths.MountPath("ses_other");

        Assert.Equal("deny", Check("edit", "write", new { path = $"{other}/ctl/x", content = "ls" }).Effect);
        Assert.Equal("deny", Check("external_directory", null, null, resources: [$"{other}/ctl/*"]).Effect);
    }

    /// <summary>
    /// The write asks about the directory first. For the session's own tree that question is the
    /// command's, and is answered when the write itself is checked.
    /// </summary>
    [Fact]
    public void TheSessionsOwnTreeIsNotAskedAboutTwice()
    {
        Assert.Equal("allow", Check("external_directory", null, null, resources: [$"{Tree}/ctl/*"]).Effect);

        // Even for a command that will be asked about: the question belongs to the edit check,
        // whose prompt shows the command.
        Assert.Equal(
            "allow",
            Check("external_directory", "write", new { path = $"{Tree}/ctl/m", content = "make" }, [Rule("*", "ask")], [$"{Tree}/ctl/*"]).Effect);
        Assert.Equal(
            "ask",
            Check("edit", "write", new { path = $"{Tree}/ctl/m", content = "make" }, [Rule("*", "ask")], [$"{Tree}/ctl/m"]).Effect);
    }

    [Fact]
    public void ATreeIsNeitherEditedNorPatched()
    {
        Assert.Equal("deny", Check("edit", "edit", new { path = $"{Tree}/ctl/x", oldString = "a", newString = "b" }).Effect);
        Assert.Equal("deny", Check("edit", "patch", new { patchText = $"*** Begin Patch\n*** Add File: {Tree}/ctl/x\n+ls\n*** End Patch" }).Effect);
    }

    [Fact]
    public void AShellCallInTheSkillsShapeIsCheckedLikeAWrite()
    {
        string command = $"cat > {Tree}/ctl/x <<'CMD'\nsudo ls\nCMD\ncat {Tree}/cmd/x/wait; cat {Tree}/cmd/x/stdout";

        Assert.Equal("deny", Check("shell", "shell", new { command }, [Rule("*", "allow"), Rule("sudo *", "deny")]).Effect);
    }

    [Fact]
    public void ARelativePathIsResolvedAgainstTheSessionsDirectory() =>
        Assert.Equal("none", Check("edit", "write", new { path = "notes/ctl/x", content = "sudo ls" }).Effect);

    [Fact]
    public void ACheckThatDoesNotTouchTheTreesIsLeftToOpencode()
    {
        Assert.Equal("none", Check("edit", "write", new { path = "/home/agent/project/a.txt", content = "x" }).Effect);
        Assert.Equal("none", Check("shell", "shell", new { command = "dotnet build" }).Effect);
    }

    [Fact]
    public void AnAnswerIsTheJsonThePluginReads()
    {
        using JsonDocument output = JsonDocument.Parse(new OpencodeDecision("deny", "because").Json());

        Assert.Equal("deny", output.RootElement.GetProperty("effect").GetString());
        Assert.Equal("because", output.RootElement.GetProperty("message").GetString());
    }
}
