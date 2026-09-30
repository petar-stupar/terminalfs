using System.Text;
using System.Text.RegularExpressions;
using TerminalFs.Core.Internal;

namespace TerminalFs.Core.Permissions;

/// <summary>One of opencode's permission rules, as its agent resolves them.</summary>
/// <param name="Action">What it is about: <c>shell</c>, <c>edit</c>, <c>*</c>.</param>
/// <param name="Resource">The pattern it matches: a command, a path.</param>
/// <param name="Effect"><c>allow</c>, <c>ask</c> or <c>deny</c>.</param>
public sealed record OpencodeRule(string Action, string Resource, string Effect);

/// <summary>What opencode's rules say about a command, and the rule and subcommand that decided it.</summary>
/// <param name="Effect"><c>allow</c>, <c>ask</c> or <c>deny</c>.</param>
/// <param name="Rule">The rule that decided, or null when none matched and opencode's default applies.</param>
/// <param name="Subcommand">The subcommand it was decided on.</param>
public sealed record OpencodeVerdict(string Effect, OpencodeRule? Rule, string Subcommand);

/// <summary>
/// opencode's shell permission rules applied to a command, the way opencode applies them to the
/// same command run with its shell tool.
/// </summary>
/// <remarks>
/// <para>
/// opencode checks each command in a line on its own, and for each takes the <em>last</em> rule
/// whose action and resource both match: order decides, not specificity, so a project's rules win
/// over the global ones they follow. A command no rule matches is asked about. Any deny refuses
/// the line, then any ask asks, and only a line whose every command is allowed runs.
/// </para>
/// <para>
/// Its wildcard is anchored at both ends: <c>*</c> matches anything, spaces and slashes included,
/// <c>?</c> one character, and a trailing <c>" *"</c> also matches the bare command, so
/// <c>git *</c> matches <c>git</c>. <c>cd</c> and its kind are not commands to opencode — it checks
/// where they go instead — and are skipped here too.
/// </para>
/// <para>
/// opencode splits a line with a shell parser; this splits it with <see cref="Subcommands"/>,
/// which is coarser. Where the two disagree this sees more pieces, each of which has to be allowed,
/// so the difference ends in a question rather than a command nobody approved. A deny is also tried
/// against each piece with its wrappers and leading assignments taken off.
/// </para>
/// </remarks>
public static class OpencodeRules
{
    private static readonly string[] DirectoryCommands = ["cd", "chdir", "pushd", "popd"];

    /// <summary>What <paramref name="rules"/> say about running <paramref name="command"/>.</summary>
    /// <param name="command">The command line.</param>
    /// <param name="rules">The rules, in the order the agent resolved them.</param>
    /// <param name="action">The permission the commands are checked under.</param>
    /// <param name="directory">
    /// Where the command runs. A <c>cd</c> out of it is checked as opencode checks one from its
    /// shell tool: as an <c>external_directory</c> question about where it goes. Null to skip that.
    /// </param>
    public static OpencodeVerdict Decide(
        string command,
        IReadOnlyList<OpencodeRule> rules,
        string action = "shell",
        string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(rules);

        IReadOnlyList<Subcommand> pieces = Subcommands.Of(command, out _) ?? [];
        string[] all = [.. Subcommands.AllForms(command)];

        if (all.Length == 0)
        {
            return new OpencodeVerdict("ask", null, DenyRule.Normalize(command));
        }

        // Every form first, for a deny: the one outcome that should not depend on how the line was
        // split or spelled.
        foreach (string form in all)
        {
            if (Evaluate(action, form, rules) is { Effect: "deny" } denied)
            {
                return new OpencodeVerdict("deny", denied, form);
            }
        }

        OpencodeVerdict? asked = null;

        foreach (string piece in pieces.Count > 0 ? pieces.Select(piece => piece.Forms[0]) : all)
        {
            string[] words = piece.Split(' ');

            if (DirectoryCommands.Contains(words[0], StringComparer.Ordinal))
            {
                if (directory is not null && Outside(words, directory) is { } target)
                {
                    OpencodeRule? where = Evaluate("external_directory", target + "/*", rules);

                    if (where?.Effect == "deny")
                    {
                        return new OpencodeVerdict("deny", where, piece);
                    }

                    if (where is null || where.Effect != "allow")
                    {
                        asked ??= new OpencodeVerdict("ask", where, piece);
                    }
                }

                continue;
            }

            OpencodeRule? rule = Evaluate(action, piece, rules);

            if (rule is null || rule.Effect != "allow")
            {
                asked ??= new OpencodeVerdict("ask", rule, piece);
            }
        }

        return asked ?? new OpencodeVerdict("allow", null, DenyRule.Normalize(command));
    }

    /// <summary>
    /// Where a <c>cd</c> goes when that is outside <paramref name="directory"/>, or null when it
    /// stays inside or cannot be told: a variable, <c>~</c> or <c>-</c> is somewhere only the shell
    /// knows, and is treated as outside by naming it as written.
    /// </summary>
    private static string? Outside(string[] words, string directory)
    {
        string? argument = words.Skip(1).FirstOrDefault(word => !word.StartsWith('-'));

        if (string.IsNullOrEmpty(argument))
        {
            return null;
        }

        string unquoted = argument.Trim('"', '\'');

        if (unquoted.IndexOfAny(['$', '~', '`', '*', '?']) >= 0 || unquoted == "-")
        {
            return unquoted;
        }

        string target = Path.GetFullPath(unquoted, directory).TrimEnd('/');
        string root = directory.TrimEnd('/');

        return target == root || target.StartsWith(root + "/", StringComparison.Ordinal) ? null : target;
    }

    /// <summary>The last rule matching both <paramref name="action"/> and <paramref name="resource"/>.</summary>
    public static OpencodeRule? Evaluate(string action, string resource, IReadOnlyList<OpencodeRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        for (int at = rules.Count - 1; at >= 0; at--)
        {
            if (Matches(action, rules[at].Action) && Matches(resource, rules[at].Resource))
            {
                return rules[at];
            }
        }

        return null;
    }

    /// <summary>opencode's wildcard match of <paramref name="input"/> against <paramref name="pattern"/>.</summary>
    public static bool Matches(string input, string pattern)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pattern);

        var expression = new StringBuilder();

        foreach (char character in pattern.Replace('\\', '/'))
        {
            expression.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString()),
            });
        }

        string escaped = expression.ToString();

        if (escaped.EndsWith("\\ .*", StringComparison.Ordinal))
        {
            escaped = escaped[..^4] + "( .*)?";
        }

        return Regex.IsMatch(
            input.Replace('\\', '/'),
            "^" + escaped + "$",
            RegexOptions.Singleline | RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None),
            TimeSpan.FromSeconds(1));
    }
}
