using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerminalFs.Core.Permissions;

/// <summary>
/// The settings file: where it lives, how it is read, and what a fresh one contains.
/// </summary>
/// <remarks>
/// <para>
/// The file is outside the tree on purpose. A deny list served through the filesystem it governs
/// would be editable by whatever the deny list exists to restrain, and a client that can rewrite
/// the rules is not restrained by them. It is read from disk before the server binds a socket.
/// </para>
/// <para>
/// The shape is Claude Code's <c>settings.local.json</c> — <c>permissions.deny</c> holding
/// <c>Bash(...)</c> entries — so that a list already written for an agent harness can be copied
/// across whole. Only <c>permissions.deny</c> is read: an <c>allow</c> list would mean something
/// quite different here, where there is no human at a prompt to fall back to.
/// </para>
/// </remarks>
public static class Settings
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The rules a fresh file is written with: the commands that end a machine, take a user's
    /// authority, or destroy what cannot be rebuilt.
    /// </summary>
    /// <remarks>
    /// These are a floor, not a security boundary. A shell has too many ways of spelling the same
    /// thing for a textual list to be complete — <c>$(which sudo)</c> is not <c>sudo</c> — and
    /// anyone relying on this list to contain a hostile caller has misread what it is. It exists
    /// to stop an agent from doing by accident what no one asked it to do.
    /// </remarks>
    public static IReadOnlyList<string> Defaults { get; } =
    [
        "Bash(sudo:*)",
        "Bash(su:*)",
        "Bash(doas:*)",
        "Bash(rm -rf /*)",
        "Bash(rm -rf ~*)",
        "Bash(rm -rf $HOME*)",
        "Bash(shutdown:*)",
        "Bash(reboot:*)",
        "Bash(halt:*)",
        "Bash(mkfs*)",
        "Bash(dd:*)",
        "Bash(diskutil:*)",
        "Bash(git push --force:*)",
        "Bash(git push -f:*)",
        "Bash(* | sh)",
        "Bash(* | bash)",
        "Bash(chmod -R 777 /*)",
    ];

    /// <summary>
    /// Where the file lives when no flag says otherwise:
    /// <c>$XDG_CONFIG_HOME/terminalfs/settings.json</c>, falling back to
    /// <c>~/.config/terminalfs/settings.json</c>.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

            string root = string.IsNullOrEmpty(configured)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config")
                : configured;

            return Path.Combine(root, "terminalfs", "settings.json");
        }
    }

    /// <summary>Reads the deny list <paramref name="path"/> holds.</summary>
    /// <exception cref="CommandException">
    /// The file is missing, unreadable, not JSON, or carries an entry that would never match.
    /// </exception>
    public static DenyList Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string text;

        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            throw new CommandException("there is no settings file there", CommandErrno.NotFound);
        }
        catch (DirectoryNotFoundException)
        {
            throw new CommandException("there is no settings file there", CommandErrno.NotFound);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CommandException(exception.Message, exception);
        }

        return Read(text);
    }

    /// <summary>Reads the deny list <paramref name="json"/> holds.</summary>
    /// <exception cref="CommandException">The text is not a settings file.</exception>
    public static DenyList Read(string json)
    {
        SettingsFile? file;

        try
        {
            file = JsonSerializer.Deserialize<SettingsFile>(json, ReadOptions);
        }
        catch (JsonException exception)
        {
            throw new CommandException(exception.Message, exception);
        }

        string[] entries = file?.Permissions?.Deny ?? [];

        return DenyList.Of(entries);
    }

    /// <summary>
    /// Writes a fresh settings file at <paramref name="path"/>, creating the directory it needs.
    /// </summary>
    /// <exception cref="CommandException">A file is already there.</exception>
    public static void WriteDefaults(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (File.Exists(path))
        {
            throw new CommandException(
                "a settings file is already there; it was left as it is",
                CommandErrno.Exists);
        }

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var fresh = new SettingsFile(new PermissionsSection([.. Defaults]));

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(fresh, WriteOptions) + Environment.NewLine);
    }

    private sealed record SettingsFile(
        [property: JsonPropertyName("permissions")] PermissionsSection? Permissions);

    private sealed record PermissionsSection(
        [property: JsonPropertyName("deny")] string[]? Deny);
}
