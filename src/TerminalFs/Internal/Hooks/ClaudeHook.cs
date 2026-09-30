using System.Text;
using System.Text.Json;
using TerminalFs.Core.Permissions;
using TerminalFs.Internal.Sessions;

namespace TerminalFs.Internal.Hooks;

/// <summary>What Claude Code sends a hook on standard input, as much of it as is read here.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="WorkingDirectory">Where the session is.</param>
/// <param name="PermissionMode">Its permission mode, where the event carries one.</param>
/// <param name="ToolName">The tool about to be called, for <c>PreToolUse</c>.</param>
/// <param name="ToolInput">What it was called with.</param>
internal sealed record ClaudeHookInput(
    string SessionId,
    string WorkingDirectory,
    string? PermissionMode,
    string? ToolName,
    JsonElement ToolInput)
{
    /// <summary>Reads the JSON Claude Code wrote.</summary>
    /// <exception cref="JsonException">It is not a hook's input.</exception>
    internal static ClaudeHookInput Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new ClaudeHookInput(
            Text(root, "session_id") ?? throw new JsonException("the hook input has no session_id"),
            Text(root, "cwd") ?? Environment.CurrentDirectory,
            Text(root, "permission_mode"),
            Text(root, "tool_name"),
            root.TryGetProperty("tool_input", out JsonElement input) ? input.Clone() : default);
    }

    /// <summary>A string field of the tool's input, or null.</summary>
    internal string? Tool(string name) => Text(ToolInput, name);

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>What the hook tells Claude Code about a tool call.</summary>
/// <param name="Decision"><c>allow</c>, <c>ask</c> or <c>deny</c>.</param>
/// <param name="Reason">Shown to the person for <c>ask</c>, and to the model for <c>deny</c>.</param>
internal sealed record ClaudeDecision(string Decision, string Reason)
{
    /// <summary>The JSON Claude Code reads a <c>PreToolUse</c> decision from.</summary>
    internal string Json()
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("hookSpecificOutput");
            writer.WriteString("hookEventName", "PreToolUse");
            writer.WriteString("permissionDecision", Decision);
            writer.WriteString("permissionDecisionReason", Reason);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

/// <summary>
/// The Claude Code hooks: a tree for each session, and the session's own permission rules applied
/// to what it runs through that tree.
/// </summary>
/// <remarks>
/// <para>
/// Claude Code checks its rules against the tool call, and a command run through a tree is not the
/// tool call: it is the text inside it, written to a file. So an allow rule for <c>cat</c> would
/// approve <c>cat &gt; ctl/x</c> whatever <c>x</c> ran. This reads the command back out and checks
/// it against the same rules, from the same files, in the same order, and its answer outranks an
/// allow: Claude Code takes a hook's <c>ask</c> or <c>deny</c> over any allow rule, and still
/// applies its own deny and ask rules to the call whatever the hook says.
/// </para>
/// <para>
/// What no rule decides follows the session's permission mode. The one mode that is not decided
/// here is <c>auto</c>: its classifier reads the whole Bash call, command included, and approving
/// or refusing what it sees is exactly its job.
/// </para>
/// </remarks>
internal sealed class ClaudeHook(SessionPaths paths, Func<string, string?> environment, string home, string managedDirectory)
{
    /// <summary>The JSON a <c>SessionStart</c> hook adds <paramref name="context"/> to the session with.</summary>
    internal static string SessionContext(string context)
    {
        var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("hookSpecificOutput");
            writer.WriteString("hookEventName", "SessionStart");
            writer.WriteString("additionalContext", context);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The decision for one <c>PreToolUse</c> call, or null to leave it to Claude Code.</summary>
    internal ClaudeDecision? PreToolUse(ClaudeHookInput input)
    {
        if (!SessionPaths.IsValidId(input.SessionId))
        {
            return null;
        }

        TreeCall call = input.ToolName switch
        {
            "Bash" => TreeCalls.Bash(input.Tool("command") ?? string.Empty, paths, input.SessionId, Spellings()),
            "Write" when input.Tool("file_path") is { } path =>
                TreeCalls.Write(path, input.Tool("content") ?? string.Empty, paths, input.SessionId),
            _ when input.Tool("file_path") is { } path => TreeCalls.Edit(path, paths),
            _ when input.Tool("notebook_path") is { } path => TreeCalls.Edit(path, paths),
            _ => new TreeCall.Elsewhere(),
        };

        return call switch
        {
            TreeCall.Refused refused => new ClaudeDecision("deny", "terminalfs: " + refused.Reason),
            TreeCall.Harmless => new ClaudeDecision("allow", "terminalfs: only reads or ends commands in this session's tree"),
            TreeCall.Run run => Decide(run, input),
            _ => null,
        };
    }

    private ClaudeDecision? Decide(TreeCall.Run run, ClaudeHookInput input)
    {
        string mode = input.PermissionMode ?? "default";
        string shown = OneLine(run.Command);

        // Before any rule: a plan is agreed before anything runs, and an allow rule is about what
        // may run once it is.
        if (mode == "plan")
        {
            return new ClaudeDecision(
                "deny",
                $"terminalfs: nothing runs through the tree in plan mode, so '{shown}' did not. It can run once the plan is approved");
        }

        string project = environment("CLAUDE_PROJECT_DIR") is { Length: > 0 } configured && Path.IsPathRooted(configured)
            ? configured
            : input.WorkingDirectory;

        ClaudeRules read = ClaudeSettings.Read(project, environment, home, managedDirectory);

        // A file that exists and cannot be read may hold the rule that would have refused this.
        // Nobody is asked to guess which: the person at the prompt decides, or in dontAsk nobody.
        if (read.Unreadable.Count > 0)
        {
            string why = $"terminalfs could not read {string.Join("; ", read.Unreadable)}";

            return mode == "dontAsk"
                ? new ClaudeDecision("deny", $"{why}, so '{shown}' was not checked and was not run")
                : new ClaudeDecision("ask", $"{why}. Run '{shown}' anyway?");
        }

        RuleVerdict? verdict = read.Rules.Decide(run.Command);

        return verdict?.Kind switch
        {
            RuleKind.Deny => new ClaudeDecision(
                "deny",
                $"terminalfs: '{shown}' is denied by '{verdict.Rule}' in {verdict.Origin}"),
            RuleKind.Ask => new ClaudeDecision(
                "ask",
                $"'{verdict.Rule}' in {verdict.Origin} asks before this runs through terminalfs: {shown}"),
            RuleKind.Allow => new ClaudeDecision(
                "allow",
                $"terminalfs: allowed by '{verdict.Rule}' in {verdict.Origin}"),
            _ => mode switch
            {
                "bypassPermissions" => new ClaudeDecision("allow", "terminalfs: no rule applies, and permissions are bypassed"),
                "dontAsk" => new ClaudeDecision(
                    "deny",
                    $"terminalfs: no permission rule allows '{shown}', and this session does not ask"),
                "auto" => null,
                _ => new ClaudeDecision("ask", $"Run through terminalfs: {shown}"),
            },
        };
    }

    /// <summary>
    /// The sessions' directory written through the variables it came from, or <c>~</c>. A command
    /// spelled that way reaches the tree without naming it, so it is refused rather than missed.
    /// </summary>
    private List<string> Spellings()
    {
        var spellings = new List<string>();

        foreach (string variable in (string[])["TERMINALFS_RUNTIME_DIR", "XDG_RUNTIME_DIR", "XDG_CACHE_HOME", "HOME"])
        {
            string? value = variable == "HOME" ? home : environment(variable);

            if (string.IsNullOrEmpty(value) || !paths.Root.StartsWith(value.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                continue;
            }

            string rest = paths.Root[value.TrimEnd('/').Length..];

            spellings.Add("$" + variable + rest);
            spellings.Add("${" + variable + "}" + rest);

            if (variable == "HOME")
            {
                spellings.Add("~" + rest);
            }
        }

        return spellings;
    }

    private static string OneLine(string command)
    {
        string trimmed = command.Trim();
        int newline = trimmed.IndexOf('\n', StringComparison.Ordinal);

        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd() + " …";
    }
}
