using System.Text;
using System.Text.Json;
using TerminalFs.Core.Permissions;
using TerminalFs.Internal.Sessions;

namespace TerminalFs.Internal.Hooks;

/// <summary>A tool call the plugin saw, which a permission check may be for.</summary>
/// <param name="Tool">The tool's name.</param>
/// <param name="Input">What it was called with.</param>
internal sealed record OpencodeCall(string? Tool, JsonElement Input)
{
    /// <summary>A string field of the input, or null.</summary>
    internal string? Field(string name) =>
        Input.ValueKind == JsonValueKind.Object
        && Input.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>What the opencode plugin asks about one permission check.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Directory">The directory the session works in, which a relative path is resolved against.</param>
/// <param name="Action">The permission opencode is checking: <c>edit</c>, <c>shell</c>, <c>external_directory</c>.</param>
/// <param name="Resources">What it is checking it on.</param>
/// <param name="Calls">
/// The tool calls under way that the check can be for. Usually one; several when a script run by
/// <c>execute</c> makes calls at once, which all carry its id.
/// </param>
/// <param name="Rules">The session's permission rules, in the order its agent resolved them.</param>
internal sealed record OpencodeCheck(
    string SessionId,
    string Directory,
    string Action,
    IReadOnlyList<string> Resources,
    IReadOnlyList<OpencodeCall> Calls,
    IReadOnlyList<OpencodeRule> Rules)
{
    /// <summary>Reads what the plugin wrote.</summary>
    /// <exception cref="JsonException">It is not a check.</exception>
    internal static OpencodeCheck Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        IReadOnlyList<OpencodeCall> calls =
            root.TryGetProperty("calls", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array
                ? [.. listed.EnumerateArray().Select(call => new OpencodeCall(
                    Text(call, "tool"),
                    call.TryGetProperty("input", out JsonElement input) ? input.Clone() : default))]
                : Text(root, "tool") is not null || root.TryGetProperty("input", out _)
                    ? [new OpencodeCall(Text(root, "tool"), root.TryGetProperty("input", out JsonElement single) ? single.Clone() : default)]
                    : [];

        return new OpencodeCheck(
            Text(root, "session_id") ?? throw new JsonException("the check has no session_id"),
            Text(root, "directory") ?? Environment.CurrentDirectory,
            Text(root, "action") ?? string.Empty,
            root.TryGetProperty("resources", out JsonElement resources) && resources.ValueKind == JsonValueKind.Array
                ? [.. resources.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
                : [],
            calls,
            root.TryGetProperty("rules", out JsonElement rules) && rules.ValueKind == JsonValueKind.Array
                ? [.. rules.EnumerateArray()
                    .Where(rule => Text(rule, "action") is not null && Text(rule, "resource") is not null && Text(rule, "effect") is not null)
                    .Select(rule => new OpencodeRule(Text(rule, "action")!, Text(rule, "resource")!, Text(rule, "effect")!))]
                : []);
    }

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
/// <param name="Call">
/// Which of the calls sent this was judged on, when it was one of them: a call refused here never
/// finishes as far as the plugin can see, and it drops the call on this.
/// </param>
internal sealed record OpencodeDecision(string Effect, string? Message = null, int? Call = null)
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

            if (Call is { } call)
            {
                writer.WriteNumber("call", call);
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
    internal static string Started(string? mountPath, string context, string root)
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
            writer.WriteString("root", root);
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

        OpencodeDecision decision = call switch
        {
            TreeCall.Refused refused => new OpencodeDecision("deny", "terminalfs: " + refused.Reason),
            TreeCall.Harmless => new OpencodeDecision("allow"),
            TreeCall.Run run => Decide(run, check.Rules, check.Directory),
            _ => OpencodeDecision.None,
        };

        // The one call a decision can be pinned on: a single write, or a single shell command.
        OpencodeCall[] judged = [.. check.Calls.Where(pending =>
            check.Action == "edit" ? pending.Field("content") is not null : pending.Field("command") is not null)];
        int index = judged.Length == 1 ? check.Calls.ToList().IndexOf(judged[0]) : -1;

        return index >= 0 && decision.Effect != "none" ? decision with { Call = index } : decision;
    }

    private TreeCall Classify(OpencodeCheck check)
    {
        string own = paths.MountPath(check.SessionId);

        // Where opencode resolved the file to, which is what gets written: the tool's own input can
        // spell it any way a path can be spelled, and a spelling this read differently from
        // opencode — ~ is not a directory name to opencode — would be a write nobody checked.
        string[] targets = [.. check.Resources
            .Select(resource => resource.EndsWith("/*", StringComparison.Ordinal) ? resource[..^2] : resource)
            .Select(resource => Path.GetFullPath(resource, check.Directory))];
        string[] inTrees = [.. targets.Where(target => Under(target, paths.Root))];

        if (check.Action == "external_directory")
        {
            if (inTrees.FirstOrDefault(target => !Under(target, own)) is { } other)
            {
                return new TreeCall.Refused($"{other} is not this session's tree; this session uses only its own, {own}");
            }

            // The session's own tree, and nothing else in this question, is answered here; what is
            // written there is checked when opencode checks the write itself, whose prompt shows
            // the command. Any other directory in the same question stays opencode's to ask about.
            return inTrees.Length > 0 && inTrees.Length == targets.Length
                ? new TreeCall.Harmless()
                : new TreeCall.Elsewhere();
        }

        if (check.Action == "edit")
        {
            // A write is whatever sets a file's whole content, by whatever name: a plugin can put
            // the built-in write under another one, to call it from code mode.
            OpencodeCall[] writes = [.. check.Calls.Where(call =>
                call.Field("content") is not null && call.Tool is not ("edit" or "patch" or "apply_patch"))];

            foreach (string target in inTrees)
            {
                // The write this check is for is the one write under way. Several at once are not
                // matched to it by path: a path can be spelled so that it reads one way here and
                // another way to opencode, and a wrong match would check one command and run
                // another.
                // An edit or a patch under way at the same time could be what this check is about,
                // and neither is let into a tree.
                TreeCall call = check.Calls.Any(pending => pending.Tool is "edit" or "patch" or "apply_patch")
                    ? TreeCalls.Edit(target, paths)
                    : writes.Length switch
                    {
                        1 => TreeCalls.Write(target, writes[0].Field("content")!, paths, check.SessionId),
                        0 => TreeCalls.Edit(target, paths),
                        _ => new TreeCall.Refused(
                            "several writes were under way at once. Write a session tree's files one at a time, "
                            + "awaiting each before the next"),
                    };

                if (call is not TreeCall.Elsewhere)
                {
                    return call;
                }
            }

            // A patch names its files in its text too; one naming a tree is refused whether or not
            // opencode's resources said so.
            return check.Calls.Any(call => call.Tool is "patch" or "apply_patch"
                && (call.Field("patchText") ?? string.Empty).Contains(paths.Root, StringComparison.Ordinal))
                    ? new TreeCall.Refused("nothing in a session tree is patched. Write a command to ctl/<name> with the write tool")
                    : new TreeCall.Elsewhere();
        }

        OpencodeCall[] shells = [.. check.Calls.Where(call => call.Field("command") is not null)];

        if (check.Action != "shell" && !shells.Any(call => call.Tool is "shell" or "bash"))
        {
            return new TreeCall.Elsewhere();
        }

        TreeCall[] found = [.. shells.Select(call =>
            TreeCalls.Bash(call.Field("command")!, paths, check.SessionId, TreeCalls.Spellings(paths, environment, home)))];

        return found.Length switch
        {
            0 => new TreeCall.Elsewhere(),
            1 => found[0],
            _ => found.All(call => call is TreeCall.Elsewhere)
                ? new TreeCall.Elsewhere()
                : new TreeCall.Refused(
                    "several shell commands were run at once and this one cannot be told from the others. Run "
                    + "commands that reach a session tree one at a time"),
        };
    }

    private static bool Under(string path, string directory) =>
        path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static OpencodeDecision Decide(TreeCall.Run run, IReadOnlyList<OpencodeRule> rules, string directory)
    {
        string shown = OneLine(run.Command);
        OpencodeVerdict verdict = OpencodeRules.Decide(run.Command, rules, directory: directory);

        return verdict.Effect switch
        {
            "deny" => new OpencodeDecision(
                "deny",
                $"terminalfs: '{shown}' is denied by the {verdict.Rule!.Action} rule '{verdict.Rule.Resource}': {verdict.Rule.Effect}"),
            "ask" => new OpencodeDecision("ask", $"terminalfs: no permission rule allows or denies '{shown}', so it needs approval to run through this session's tree"),
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
