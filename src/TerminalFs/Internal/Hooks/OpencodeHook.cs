using System.Text;
using System.Text.Json;
using TerminalFs.Core.Permissions;
using TerminalFs.Internal.Sessions;

namespace TerminalFs.Internal.Hooks;

/// <summary>What the opencode plugin asks about one permission check.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Directory">The directory the session works in, which a relative path is resolved against.</param>
/// <param name="Action">The permission opencode is checking: <c>edit</c>, <c>shell</c>, <c>external_directory</c>.</param>
/// <param name="Resources">What it is checking it on.</param>
/// <param name="Tool">The tool whose call this check is for, when the plugin saw it.</param>
/// <param name="Input">What the tool was called with.</param>
/// <param name="Rules">The session's permission rules, in the order its agent resolved them.</param>
internal sealed record OpencodeCheck(
    string SessionId,
    string Directory,
    string Action,
    IReadOnlyList<string> Resources,
    string? Tool,
    JsonElement Input,
    IReadOnlyList<OpencodeRule> Rules)
{
    /// <summary>Reads what the plugin wrote.</summary>
    /// <exception cref="JsonException">It is not a check.</exception>
    internal static OpencodeCheck Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new OpencodeCheck(
            Text(root, "session_id") ?? throw new JsonException("the check has no session_id"),
            Text(root, "directory") ?? Environment.CurrentDirectory,
            Text(root, "action") ?? string.Empty,
            root.TryGetProperty("resources", out JsonElement resources) && resources.ValueKind == JsonValueKind.Array
                ? [.. resources.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
                : [],
            Text(root, "tool"),
            root.TryGetProperty("input", out JsonElement input) ? input.Clone() : default,
            root.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array
                ? [.. rules.EnumerateArray()
                    .Where(rule => Text(rule, "action") is not null && Text(rule, "resource") is not null && Text(rule, "effect") is not null)
                    .Select(rule => new OpencodeRule(Text(rule, "action")!, Text(rule, "resource")!, Text(rule, "effect")!))]
                : []);
    }

    /// <summary>A string field of the tool's input, or null.</summary>
    internal string? Field(string name) => Text(Input, name);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>What the plugin sets the check's effect to, or <c>none</c> to leave opencode's.</summary>
/// <param name="Effect"><c>allow</c>, <c>ask</c>, <c>deny</c> or <c>none</c>.</param>
/// <param name="Message">Why, for a refusal or a question.</param>
internal sealed record OpencodeDecision(string Effect, string? Message = null)
{
    internal static OpencodeDecision None { get; } = new("none");

    /// <summary>The JSON the plugin reads.</summary>
    internal string Json()
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("effect", Effect);

            if (Message is not null)
            {
                writer.WriteString("message", Message);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// The opencode side of the hooks: the session's own opencode permission rules applied to what it
/// runs through its tree, answered to the terminalfs plugin, which applies the answer inside
/// opencode's own permission check.
/// </summary>
/// <remarks>
/// <para>
/// opencode checks a write to <c>ctl/&lt;name&gt;</c> as an edit of a file, and its shell rules
/// never see the command in it. The plugin hooks that edit check, hands it here with the call it
/// belongs to, and sets the check's effect to what comes back: a deny refuses the write with the
/// reason, an ask shows opencode's own prompt — the write's diff, which is the command — and an
/// allow lets it through.
/// </para>
/// <para>
/// Which call reached the tree how is <see cref="TreeCalls"/>'s business, as it is for Claude
/// Code; only the rules differ, and <see cref="OpencodeRules"/> reads them as opencode does. opencode
/// has no permission modes of its own to follow: an agent's rules already say what it may do.
/// </para>
/// </remarks>
internal sealed class OpencodeHook(SessionPaths paths, Func<string, string?> environment, string home)
{
    /// <summary>What <c>session-start</c> tells the plugin: where the tree is, and what to tell the agent.</summary>
    internal static string Started(string? mountPath, string context)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            if (mountPath is not null)
            {
                writer.WriteString("mount", mountPath);
            }

            writer.WriteString("context", context);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>What to set the check's effect to.</summary>
    internal OpencodeDecision Check(OpencodeCheck check)
    {
        if (!SessionPaths.IsValidId(check.SessionId))
        {
            return OpencodeDecision.None;
        }

        TreeCall call = Classify(check);

        return call switch
        {
            TreeCall.Refused refused => new OpencodeDecision("deny", "terminalfs: " + refused.Reason),
            TreeCall.Harmless => new OpencodeDecision("allow"),
            TreeCall.Run run => Decide(run, check.Rules),
            _ => OpencodeDecision.None,
        };
    }

    private TreeCall Classify(OpencodeCheck check)
    {
        string? Resolved(string? path) =>
            string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, check.Directory);

        // The directory question comes first and shows only the directory. The command's own
        // question is left to the edit check that follows, whose prompt shows it as the diff.
        switch (check.Action == "external_directory" ? null : check.Tool)
        {
            case "write" when Resolved(check.Field("path") ?? check.Field("filePath")) is { } path:
                return TreeCalls.Write(path, check.Field("content") ?? string.Empty, paths, check.SessionId);

            case "edit" when Resolved(check.Field("path") ?? check.Field("filePath")) is { } path:
                return TreeCalls.Edit(path, paths);

            case "patch" or "apply_patch":
                return (check.Field("patchText") ?? string.Empty).Contains(paths.Root, StringComparison.Ordinal)
                    ? new TreeCall.Refused("nothing in a session tree is patched. Write a command to ctl/<name> with the write tool")
                    : new TreeCall.Elsewhere();

            case "shell" or "bash" when check.Field("command") is { } command:
                return TreeCalls.Bash(command, paths, check.SessionId, TreeCalls.Spellings(paths, environment, home));
        }

        // A check whose call the plugin did not see, such as the external_directory check a write
        // outside the project makes first: judged on where it points.
        string own = paths.MountPath(check.SessionId);
        TreeCall result = new TreeCall.Elsewhere();

        foreach (string resource in check.Resources)
        {
            string path = resource.EndsWith("/*", StringComparison.Ordinal) ? resource[..^2] : resource;

            if (!path.StartsWith(paths.Root + "/", StringComparison.Ordinal) && path != paths.Root)
            {
                continue;
            }

            if (path != own && !path.StartsWith(own + "/", StringComparison.Ordinal))
            {
                return new TreeCall.Refused($"{path} is not this session's tree; this session uses only its own, {own}");
            }

            // The directory is this session's own. What is written there is checked when opencode
            // checks the write itself, so this one question is not asked twice.
            if (check.Action == "external_directory")
            {
                result = new TreeCall.Harmless();
            }
        }

        return result;
    }

    private static OpencodeDecision Decide(TreeCall.Run run, IReadOnlyList<OpencodeRule> rules)
    {
        string shown = OneLine(run.Command);
        OpencodeVerdict verdict = OpencodeRules.Decide(run.Command, rules);

        return verdict.Effect switch
        {
            "deny" => new OpencodeDecision(
                "deny",
                $"terminalfs: '{shown}' is denied by the {verdict.Rule!.Action} rule '{verdict.Rule.Resource}': {verdict.Rule.Effect}"),
            "ask" => new OpencodeDecision("ask", $"Run through terminalfs: {shown}"),
            _ => new OpencodeDecision("allow"),
        };
    }

    private static string OneLine(string command)
    {
        string trimmed = command.Trim();
        int newline = trimmed.IndexOf('\n', StringComparison.Ordinal);

        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd() + " …";
    }
}
