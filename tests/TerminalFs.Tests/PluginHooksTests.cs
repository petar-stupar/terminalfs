using System.Text.Json;

namespace TerminalFs.Tests;

/// <summary>
/// The Claude Code plugin's hooks name subcommands of this program. One that names a subcommand
/// the program does not have fails at the harness, which reports it and carries on without the
/// check — so the pairing is pinned here instead.
/// </summary>
public sealed class PluginHooksTests
{
    private static JsonElement Hooks()
    {
        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            string path = Path.Combine(at.FullName, "plugins", "terminalfs", "hooks", "hooks.json");

            if (File.Exists(path))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

                return document.RootElement.GetProperty("hooks").Clone();
            }
        }

        throw new InvalidOperationException("the tests are not running inside the repository");
    }

    private static IEnumerable<(string Event, JsonElement Group, JsonElement Hook)> Each() =>
        from @event in Hooks().EnumerateObject()
        from @group in @event.Value.EnumerateArray()
        from hook in @group.GetProperty("hooks").EnumerateArray()
        select (@event.Name, @group, hook);

    [Fact]
    public void EveryHookRunsASubcommandThisProgramHas()
    {
        var events = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SessionStart"] = "session-start",
            ["SessionEnd"] = "session-end",
            ["PreToolUse"] = "pre-tool-use",
        };

        Assert.Equal(events.Keys.Order(StringComparer.Ordinal), Hooks().EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));

        foreach ((string name, _, JsonElement hook) in Each())
        {
            Assert.Equal("terminalfs", hook.GetProperty("command").GetString());
            Assert.Equal(
                ["hook", "claude", events[name]],
                hook.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()));
            Assert.Contains($"terminalfs hook claude {events[name]}", Program.HookUsage, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every tool that can put a command into a tree has to pass the check: one left out is a way
    /// round it.
    /// </summary>
    [Fact]
    public void TheCheckSeesEveryToolThatCanWriteAFile()
    {
        (_, JsonElement group, _) = Each().Single(hook => hook.Event == "PreToolUse");
        string[] tools = group.GetProperty("matcher").GetString()!.Split('|');

        Assert.Superset(new HashSet<string>(["Bash", "Write", "Edit"], StringComparer.Ordinal), new HashSet<string>(tools, StringComparer.Ordinal));
    }

    /// <summary>The opencode plugin runs the same program, and the same pairing holds.</summary>
    [Fact]
    public void TheOpencodePluginRunsSubcommandsThisProgramHas()
    {
        string plugin = string.Empty;

        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null && plugin.Length == 0; at = at.Parent)
        {
            string path = Path.Combine(at.FullName, "plugins", "opencode", "index.js");

            if (File.Exists(path))
            {
                plugin = File.ReadAllText(path);
            }
        }

        Assert.Contains("[\"hook\", \"opencode\", event]", plugin, StringComparison.Ordinal);

        foreach (string @event in (string[])["session-start", "session-end", "check"])
        {
            Assert.Contains($"terminalfs(\"{@event}\"", plugin, StringComparison.Ordinal);
            Assert.Contains($"terminalfs hook opencode {@event}", Program.HookUsage, StringComparison.Ordinal);
        }
    }
}
