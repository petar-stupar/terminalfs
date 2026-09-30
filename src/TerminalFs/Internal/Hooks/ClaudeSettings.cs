using System.Text.Json;
using TerminalFs.Core.Permissions;

namespace TerminalFs.Internal.Hooks;

/// <summary>The rules Claude Code's settings files hold, and the files that could not be read.</summary>
/// <param name="Rules">Every <c>Bash</c> rule in force, merged.</param>
/// <param name="Unreadable">A file that exists and could not be read, with why, for each one.</param>
internal sealed record ClaudeRules(PermissionRules Rules, IReadOnlyList<string> Unreadable);

/// <summary>
/// Where Claude Code keeps its settings, and the permission rules in them.
/// </summary>
/// <remarks>
/// <para>
/// Four levels, as Claude Code reads them: managed (<c>managed-settings.json</c> and the
/// <c>managed-settings.d</c> drop-ins beside it, in the system directory for this platform), user
/// (<c>$CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>), project (<c>.claude/settings.json</c> in the
/// project directory) and local (<c>.claude/settings.local.json</c> in the project directory, and
/// at the root of the git repository it is in, which is where Claude Code saves an approval). Rules
/// from all of them apply together; which file a rule came from matters only for saying so.
/// </para>
/// <para>
/// Managed settings can say that only their own rules count, and then the others are not read.
/// </para>
/// </remarks>
internal static class ClaudeSettings
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The system directory managed settings are deployed to on this platform.</summary>
    internal static string ManagedDirectory =>
        OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode"
        : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode"
        : "/etc/claude-code";

    /// <summary>The rules in force for a session whose project is <paramref name="projectDirectory"/>.</summary>
    /// <param name="projectDirectory">The session's project directory.</param>
    /// <param name="environment">Reads an environment variable.</param>
    /// <param name="home">The user's home directory.</param>
    /// <param name="managedDirectory">Where managed settings live; <see cref="ManagedDirectory"/> but for tests.</param>
    internal static ClaudeRules Read(
        string projectDirectory,
        Func<string, string?> environment,
        string home,
        string managedDirectory)
    {
        var unreadable = new List<string>();
        var managed = new List<RuleSource>();
        bool managedOnly = false;

        foreach (string path in ManagedFiles(managedDirectory, unreadable))
        {
            if (Load(path, unreadable) is { } file)
            {
                managed.Add(file.Source);
                managedOnly |= file.ManagedOnly;
            }
        }

        if (managedOnly)
        {
            return new ClaudeRules(PermissionRules.Of(managed), unreadable);
        }

        var sources = new List<RuleSource>(managed);

        string shared = Path.GetFullPath(Path.Combine(projectDirectory, ".claude", "settings.json"));
        bool trusted = Trusted(projectDirectory, environment, home);

        foreach (string path in OtherFiles(projectDirectory, environment, home))
        {
            if (Load(path, unreadable) is not { } file)
            {
                continue;
            }

            // A project's own allow rules grant what the repository asks for, so Claude Code holds
            // them until somebody has trusted the folder; its deny and ask rules only restrict.
            sources.Add(!trusted && string.Equals(Path.GetFullPath(path), shared, StringComparison.Ordinal)
                ? file.Source with { Allow = [] }
                : file.Source);
        }

        return new ClaudeRules(PermissionRules.Of(sources), unreadable);
    }

    private static List<string> ManagedFiles(string directory, List<string> unreadable)
    {
        var files = new List<string> { Path.Combine(directory, "managed-settings.json") };
        string dropIns = Path.Combine(directory, "managed-settings.d");

        try
        {
            if (Directory.Exists(dropIns))
            {
                // Alphabetical, and not hidden: the order Claude Code merges them in.
                files.AddRange(Directory.EnumerateFiles(dropIns, "*.json")
                    .Where(path => !Path.GetFileName(path).StartsWith('.'))
                    .Order(StringComparer.Ordinal));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{dropIns}: {exception.Message}");
        }

        return files;
    }

    private static IEnumerable<string> OtherFiles(string projectDirectory, Func<string, string?> environment, string home)
    {
        string? configured = environment("CLAUDE_CONFIG_DIR");
        string user = !string.IsNullOrEmpty(configured) && Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(home, ".claude");

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string path in (string[])
        [
            Path.Combine(user, "settings.json"),
            Path.Combine(projectDirectory, ".claude", "settings.json"),
            Path.Combine(projectDirectory, ".claude", "settings.local.json"),
        ])
        {
            if (seen.Add(Path.GetFullPath(path)))
            {
                yield return path;
            }
        }

        // Where an approval given in a subdirectory, or in a worktree, is saved. Not in a home
        // directory that is itself a repository, where Claude Code keeps the file with the project.
        if (RepositoryRoot(projectDirectory) is { } root
            && !string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(home), StringComparison.Ordinal))
        {
            string local = Path.Combine(root, ".claude", "settings.local.json");

            if (seen.Add(Path.GetFullPath(local)))
            {
                yield return local;
            }
        }
    }

    /// <summary>
    /// Whether somebody has accepted Claude Code's trust dialog for the folder a project's rules
    /// come from: the repository's root, or outside a repository the directory itself or one above
    /// it.
    /// </summary>
    internal static bool Trusted(string projectDirectory, Func<string, string?> environment, string home)
    {
        string? configured = environment("CLAUDE_CONFIG_DIR");
        string state = !string.IsNullOrEmpty(configured) && Path.IsPathRooted(configured)
            ? Path.Combine(configured, ".claude.json")
            : Path.Combine(home, ".claude.json");

        JsonElement projects;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(state), ReadOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("projects", out JsonElement found)
                || found.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            projects = found.Clone();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }

        string? repository = RepositoryRoot(projectDirectory);
        IEnumerable<string> keys = repository is not null
            ? [repository]
            : Ancestors(Path.GetFullPath(projectDirectory));

        return keys.Any(key =>
            projects.TryGetProperty(Path.TrimEndingDirectorySeparator(key), out JsonElement project)
            && project.ValueKind == JsonValueKind.Object
            && project.TryGetProperty("hasTrustDialogAccepted", out JsonElement accepted)
            && accepted.ValueKind == JsonValueKind.True);
    }

    private static IEnumerable<string> Ancestors(string directory)
    {
        for (string? at = directory; at is not null; at = Path.GetDirectoryName(at))
        {
            yield return at;
        }
    }

    /// <summary>
    /// The working tree of the main checkout of the repository <paramref name="directory"/> is in,
    /// or null outside one. A worktree's <c>.git</c> is a file naming its git directory, whose
    /// <c>commondir</c> names the main one.
    /// </summary>
    internal static string? RepositoryRoot(string directory)
    {
        for (string? at = Path.GetFullPath(directory); at is not null; at = Path.GetDirectoryName(at))
        {
            string dotGit = Path.Combine(at, ".git");

            if (Directory.Exists(dotGit))
            {
                return at;
            }

            if (!File.Exists(dotGit))
            {
                continue;
            }

            try
            {
                string pointer = File.ReadAllText(dotGit).Trim();

                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return at;
                }

                string gitDirectory = Path.GetFullPath(Path.Combine(at, pointer["gitdir:".Length..].Trim()));
                string commonFile = Path.Combine(gitDirectory, "commondir");

                if (!File.Exists(commonFile))
                {
                    return at;
                }

                string common = Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(commonFile).Trim()));

                return string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(common)), ".git", StringComparison.Ordinal)
                    ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(common))
                    : at;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return at;
            }
        }

        return null;
    }

    private static (RuleSource Source, bool ManagedOnly)? Load(string path, List<string> unreadable)
    {
        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"{path}: {exception.Message}");

            return null;
        }

        try
        {
            return Parse(path, text);
        }
        catch (JsonException exception)
        {
            unreadable.Add($"{path}: {exception.Message}");

            return null;
        }
    }

    /// <summary>The rules one settings file holds.</summary>
    /// <exception cref="JsonException">It is not JSON.</exception>
    internal static (RuleSource Source, bool ManagedOnly) Parse(string origin, string json)
    {
        using var document = JsonDocument.Parse(json, ReadOptions);

        JsonElement root = document.RootElement;
        bool managedOnly = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("allowManagedPermissionRulesOnly", out JsonElement only)
            && only.ValueKind == JsonValueKind.True;

        JsonElement permissions = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("permissions", out JsonElement found)
            && found.ValueKind == JsonValueKind.Object
            ? found
            : default;

        return (new RuleSource(origin, List(permissions, "allow"), List(permissions, "ask"), List(permissions, "deny")), managedOnly);
    }

    private static string[] List(JsonElement permissions, string name) =>
        permissions.ValueKind == JsonValueKind.Object
        && permissions.TryGetProperty(name, out JsonElement list)
        && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String).Select(entry => entry.GetString()!)]
            : [];
}
