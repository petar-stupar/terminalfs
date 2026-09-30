using System.Reflection;
using TerminalFs.Core;

namespace TerminalFs.Internal.Plugins;

/// <summary>An agent harness this program carries a plugin for.</summary>
internal enum PluginHarness
{
    /// <summary>Claude Code: a marketplace with the plugin in it, for Claude Code to install.</summary>
    Claude,

    /// <summary>opencode: the plugin itself, in opencode's plugin directory.</summary>
    Opencode,
}

/// <summary>
/// <c>terminalfs plugin install</c>: writes out the plugin that goes with this binary.
/// </summary>
/// <remarks>
/// <para>
/// A plugin is what starts a session's tree, so it has to be on disk before any tree is, and it
/// cannot be served from one. It is carried in the binary instead, so that an installed plugin is
/// the one written for the program it runs, and its skill is rendered from the program rather than
/// copied.
/// </para>
/// <para>
/// Claude Code installs plugins only from a marketplace, which it copies into its own cache, so for
/// Claude Code this writes a marketplace laid out as the repository's is, and says how to add it.
/// </para>
/// </remarks>
internal static class Plugins
{
    private const string ClaudeSkill = "plugins/terminalfs/skills/terminalfs/SKILL.md";

    /// <summary>Where <paramref name="harness"/>'s plugin goes when nobody says.</summary>
    internal static string DefaultDirectory(PluginHarness harness, Func<string, string?> environment, string home)
    {
        return harness switch
        {
            PluginHarness.Claude => Path.Combine(Base("XDG_DATA_HOME", ".local", "share"), "terminalfs", "claude-code"),
            _ => Path.Combine(Base("XDG_CONFIG_HOME", ".config"), "opencode", "plugins", "terminalfs"),
        };

        // A relative XDG directory is to be ignored, as the specification says.
        string Base(string variable, params string[] fallback) =>
            environment(variable) is { Length: > 0 } set && Path.IsPathFullyQualified(set)
                ? set
                : Path.Combine([home, .. fallback]);
    }

    /// <summary>The files <paramref name="harness"/>'s plugin is made of, by path under its directory.</summary>
    internal static IReadOnlyDictionary<string, string> Files(PluginHarness harness)
    {
        string prefix = harness == PluginHarness.Claude ? "claude/" : "opencode/";
        Assembly assembly = typeof(Plugins).Assembly;
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            files[name[prefix.Length..]] = reader.ReadToEnd();
        }

        if (harness == PluginHarness.Claude)
        {
            files[ClaudeSkill] = PluginSkills.ClaudeCode;
        }

        return files;
    }

    /// <summary>
    /// Writes <paramref name="harness"/>'s plugin into <paramref name="directory"/>, replacing an
    /// earlier one, and returns what to tell whoever asked.
    /// </summary>
    internal static string Install(PluginHarness harness, string directory)
    {
        directory = Path.GetFullPath(directory);

        foreach ((string path, string content) in Files(harness))
        {
            string at = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(at)!);
            File.WriteAllText(at, content);
        }

        return harness == PluginHarness.Claude
            ? $"""
                wrote a Claude Code marketplace with the terminalfs plugin to {directory}. Install it with
                    claude plugin marketplace add {directory}
                    claude plugin install terminalfs@terminalfs
                and after installing a newer terminalfs, run this again and
                    claude plugin marketplace update terminalfs
                    claude plugin update terminalfs@terminalfs
                """
            : $"wrote the opencode plugin to {directory}; opencode loads it when it next starts";
    }

    internal const string Usage = """
        usage: terminalfs plugin install claude|opencode [--dir <dir>]

        Write out the plugin for an agent harness that goes with this binary: a tree for each
        session, and the session's own permission rules applied to what runs through it. Run it
        again after installing a newer terminalfs.

          claude     a Claude Code marketplace holding the plugin, and how to add it;
                     $XDG_DATA_HOME/terminalfs/claude-code by default
          opencode   the opencode plugin, where opencode finds it;
                     $XDG_CONFIG_HOME/opencode/plugins/terminalfs by default
          --dir      somewhere else
        """;
}
