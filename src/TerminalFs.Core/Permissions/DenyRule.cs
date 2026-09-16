using System.Text;

namespace TerminalFs.Core.Permissions;

/// <summary>
/// One entry of the settings file's deny list, and what it matches.
/// </summary>
/// <remarks>
/// <para>
/// The spelling is Claude Code's, so that a rule can be moved between a
/// <c>.claude/settings.local.json</c> and this file without being rewritten. Three shapes:
/// </para>
/// <list type="bullet">
/// <item><c>Bash(git push:*)</c> — a prefix. The command is <c>git push</c>, or begins with
/// <c>git push</c> followed by whitespace. The boundary is what keeps it from matching
/// <c>git pushes-nothing</c>.</item>
/// <item><c>Bash(rm -rf /*)</c> — a glob, anchored at both ends, where <c>*</c> stands for any
/// run of characters including none.</item>
/// <item><c>Bash(halt)</c> — no <c>*</c> at all, so the command must be exactly that.</item>
/// </list>
/// <para>
/// Anything that is not <c>Bash(...)</c> is not this program's business and is ignored rather
/// than refused: a settings file shared with an agent harness will carry <c>Read</c>,
/// <c>Edit</c> and <c>WebFetch</c> rules too, and refusing to start over one of those would make
/// the file unshareable. Text after the closing parenthesis is a different matter — it is a rule
/// that was meant to match something and never will, so it is an error.
/// </para>
/// </remarks>
public sealed class DenyRule
{
    private readonly string pattern;
    private readonly bool prefix;

    private DenyRule(string text, string pattern, bool prefix)
    {
        Text = text;
        this.pattern = pattern;
        this.prefix = prefix;
    }

    /// <summary>The entry as it was written, for naming in a refusal.</summary>
    public string Text { get; }

    /// <summary>
    /// Reads <paramref name="entry"/>. Returns null when the entry is a rule for some other tool,
    /// which is not an error.
    /// </summary>
    /// <exception cref="CommandException">The entry is malformed.</exception>
    public static DenyRule? Parse(string entry)
    {
        string trimmed = entry.Trim();

        if (trimmed.Length == 0)
        {
            throw new CommandException("a deny entry is empty");
        }

        int open = trimmed.IndexOf('(', StringComparison.Ordinal);

        if (open < 0)
        {
            // A bare tool name — "Bash" — denies the whole tool in a harness. Here the whole tool
            // is the only thing this program does, and a file that switches it off wholesale is
            // more likely a mistake than a wish.
            return null;
        }

        if (trimmed[^1] != ')')
        {
            throw new CommandException(
                $"'{entry}' has text after its closing parenthesis; it would never match anything");
        }

        if (!trimmed.AsSpan(0, open).Trim().Equals("Bash", StringComparison.Ordinal))
        {
            return null;
        }

        string body = trimmed[(open + 1)..^1];

        if (body.Length == 0)
        {
            throw new CommandException($"'{entry}' denies nothing; it has an empty pattern");
        }

        return body.EndsWith(":*", StringComparison.Ordinal)
            ? new DenyRule(trimmed, Normalize(body[..^2]), prefix: true)
            : new DenyRule(trimmed, Normalize(body), prefix: false);
    }

    /// <summary>Whether this rule refuses <paramref name="command"/>, already normalised.</summary>
    public bool Matches(string command) =>
        prefix ? MatchesPrefix(command) : MatchesGlob(command, pattern);

    /// <summary>
    /// Collapses runs of whitespace to one space and trims the ends, so that a rule written with
    /// single spaces still matches a command that was written with a tab or two spaces.
    /// </summary>
    internal static string Normalize(string text)
    {
        var normalized = new StringBuilder(text.Length);
        bool pendingSpace = false;

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }

    private bool MatchesPrefix(string command)
    {
        if (!command.StartsWith(pattern, StringComparison.Ordinal))
        {
            return false;
        }

        // Equal, or the next character ends the word. Without this, a rule against 'git push'
        // would also refuse 'git pushover', which nobody asked for.
        return command.Length == pattern.Length || command[pattern.Length] == ' ';
    }

    /// <summary>
    /// A two-ended anchored glob in which <c>*</c> matches any run of characters including none.
    /// Written out rather than compiled to a regular expression, because a pattern typed by a
    /// person contains characters a regular expression reads as syntax — <c>.</c>, <c>$</c>,
    /// <c>[</c> — and escaping them correctly is the bug this avoids having.
    /// </summary>
    private static bool MatchesGlob(string text, string pattern)
    {
        int textAt = 0;
        int patternAt = 0;
        int starAt = -1;
        int matchAt = 0;

        while (textAt < text.Length)
        {
            if (patternAt < pattern.Length && pattern[patternAt] == '*')
            {
                starAt = patternAt++;
                matchAt = textAt;
            }
            else if (patternAt < pattern.Length && pattern[patternAt] == text[textAt])
            {
                patternAt++;
                textAt++;
            }
            else if (starAt >= 0)
            {
                patternAt = starAt + 1;
                textAt = ++matchAt;
            }
            else
            {
                return false;
            }
        }

        while (patternAt < pattern.Length && pattern[patternAt] == '*')
        {
            patternAt++;
        }

        return patternAt == pattern.Length;
    }
}
