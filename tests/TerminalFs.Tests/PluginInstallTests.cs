using System.Diagnostics;
using System.Text.Json;
using TerminalFs.Core;
using TerminalFs.Internal.Plugins;

namespace TerminalFs.Tests;

/// <summary>
/// The binary carries the plugins that go with it. What it writes out has to be what the
/// repository holds, or an installed plugin and one added from the repository would differ.
/// </summary>
public sealed class PluginInstallTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "terminalfs-plugin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

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

    private static string[] Listed(string directory) =>
        [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Claude Code's marketplace is laid out as the repository's, so the same two commands add
    /// either, and every file in it is the repository's.
    /// </summary>
    [Fact]
    public void TheClaudeMarketplaceIsTheRepositorys()
    {
        Plugins.Install(PluginHarness.Claude, _directory);

        string[] expected =
        [
            ".claude-plugin/marketplace.json",
            "plugins/terminalfs/.claude-plugin/plugin.json",
            "plugins/terminalfs/hooks/hooks.json",
            "plugins/terminalfs/skills/terminalfs/SKILL.md",
        ];

        Assert.Equal(expected, Listed(_directory));

        foreach (string file in expected)
        {
            Assert.Equal(File.ReadAllText(Path.Combine(Repository, file)), File.ReadAllText(Path.Combine(_directory, file)));
        }
    }

    /// <summary>The opencode plugin is its script alone: the skill comes from the binary.</summary>
    [Fact]
    public void TheOpencodePluginIsItsScript()
    {
        Plugins.Install(PluginHarness.Opencode, _directory);

        Assert.Equal(["index.js"], Listed(_directory));
        Assert.Equal(
            File.ReadAllText(Path.Combine(Repository, "plugins", "opencode", "index.js")),
            File.ReadAllText(Path.Combine(_directory, "index.js")));
    }

    /// <summary>A second install, after an upgrade, replaces the first.</summary>
    [Fact]
    public void InstallingAgainReplacesThePlugin()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "index.js"), "old");

        Plugins.Install(PluginHarness.Opencode, _directory);

        Assert.NotEqual("old", File.ReadAllText(Path.Combine(_directory, "index.js")));
    }

    /// <summary>
    /// Paths are built for the platform the test runs on: <c>/data</c> is not fully qualified on
    /// Windows, where it would be ignored as a relative XDG directory is.
    /// </summary>
    [Theory]
    [InlineData(true, null, null, "home/u/.local/share/terminalfs/claude-code")]
    [InlineData(true, "XDG_DATA_HOME", "data", "data/terminalfs/claude-code")]
    [InlineData(false, null, null, "home/u/.config/opencode/plugins/terminalfs")]
    [InlineData(false, "XDG_CONFIG_HOME", "config", "config/opencode/plugins/terminalfs")]
    public void ThePluginGoesWhereItsHarnessLooks(bool claude, string? variable, string? value, string expected)
    {
        Assert.Equal(
            Rooted(expected),
            Plugins.DefaultDirectory(
                claude ? PluginHarness.Claude : PluginHarness.Opencode,
                name => name == variable && value is not null ? Rooted(value) : null,
                Rooted("home/u")));
    }

    /// <summary>A relative XDG directory is ignored, as the specification says.</summary>
    [Fact]
    public void ARelativeXdgDirectoryIsIgnored()
    {
        Assert.Equal(
            Rooted("home/u/.config/opencode/plugins/terminalfs"),
            Plugins.DefaultDirectory(
                PluginHarness.Opencode,
                name => name == "XDG_CONFIG_HOME" ? "relative" : null,
                Rooted("home/u")));
    }

    private static string Rooted(string path) =>
        Path.Combine([Path.GetPathRoot(Path.GetTempPath())!, .. path.Split('/')]);

    /// <summary>The opencode plugin asks the binary for its skill, as JSON.</summary>
    [Fact]
    public async Task TheOpencodeSkillHookAnswersWithTheSkill()
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in (string[])[Path.Combine(AppContext.BaseDirectory, "terminalfs.dll"), "hook", "opencode", "skill"])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;

        await process.StandardInput.WriteAsync("{}");
        process.StandardInput.Close();

        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        _ = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);

        using JsonDocument answer = JsonDocument.Parse(await output);

        Assert.Equal(PluginSkills.Opencode, answer.RootElement.GetProperty("content").GetString());
    }
}
