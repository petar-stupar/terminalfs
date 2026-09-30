using System.Text.Json;
using System.Xml.Linq;
using TerminalFs.Core.Internal.Nodes;

namespace TerminalFs.Core.Tests;

/// <summary>
/// The Claude Code plugin ships files that are copies of what the program itself says. A copy
/// that drifts tells an agent one thing while the tree does another.
/// </summary>
public sealed class PluginTests
{
    private static string Repository
    {
        get
        {
            for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
            {
                if (File.Exists(Path.Combine(at.FullName, "terminalfs.slnx")))
                {
                    return at.FullName;
                }
            }

            throw new InvalidOperationException("the tests are not running inside the repository");
        }
    }

    private static string Plugin => Path.Combine(Repository, "plugins", "terminalfs");

    /// <summary>
    /// The plugin's skill is the one the tree serves to Claude Code when nobody has said where the
    /// tree is: the session's own path arrives from the session-start hook instead. The copy is
    /// here for the marketplace in the repository; <c>terminalfs plugin install</c> renders its own.
    /// </summary>
    [Fact]
    public void ThePluginsSkillIsTheOneTheTreeServes()
    {
        string shipped = File.ReadAllText(Path.Combine(Plugin, "skills", "terminalfs", "SKILL.md"));

        Assert.Equal(TreeText.Skill(SkillHarness.ClaudeCode, mountPath: null), shipped);
    }

    /// <summary>
    /// Claude Code keeps an installed plugin at the version it names, so a release that moved the
    /// program without the plugin would leave everybody on hooks written for the old one.
    /// </summary>
    [Fact]
    public void ThePluginIsVersionedWithTheProgram()
    {
        string version = XDocument.Load(Path.Combine(Repository, "Directory.Build.props"))
            .Descendants("Version").Single().Value;

        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Plugin, ".claude-plugin", "plugin.json")));

        Assert.Equal(version, manifest.RootElement.GetProperty("version").GetString());
    }
}
