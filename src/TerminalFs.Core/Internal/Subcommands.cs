using System.Text;
using TerminalFs.Core.Permissions;

namespace TerminalFs.Core.Internal;

/// <summary>
/// One subcommand of a shell command, in the forms a rule is matched against.
/// </summary>
/// <param name="Forms">
/// The subcommand as written, and again with its leading assignments and wrappers taken off, all
/// normalised. A deny or ask rule that matches any of them matches the subcommand.
/// </param>
/// <param name="Plain">
/// The subcommand as an allow rule sees it: wrappers taken off, but no assignment, because an
/// allow rule written for <c>npm test</c> says nothing about <c>LD_PRELOAD=x npm test</c>. Null
/// when the subcommand starts with an assignment.
/// </param>
internal sealed record Subcommand(IReadOnlyList<string> Forms, string? Plain);

/// <summary>
/// A shell command broken into the subcommands a permission rule is checked against, the way
/// Claude Code breaks it.
/// </summary>
/// <remarks>
/// <para>
/// Coarse, as <see cref="DenyList"/> is: this is a check, not a parser. It splits at
/// <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, <c>&amp;</c> and newlines without regard to
/// quoting, and takes what is inside <c>$(…)</c> and backticks as subcommands of their own. Where
/// that splits somewhere a shell would not, a deny or ask rule gets one more chance to match, and
/// an allow rule one more piece it has to cover — both of which end in a question rather than a
/// command that should not have run.
/// </para>
/// <para>
/// Wrappers that run their arguments as the command — <c>timeout 30 npm test</c> — are taken off
/// as Claude Code takes them off, so a rule written for <c>npm test</c> sees <c>npm test</c>.
/// </para>
/// </remarks>
internal static partial class Subcommands
{
    private static readonly string[] Wrappers =
        ["timeout", "time", "nice", "nohup", "stdbuf", "command", "builtin", "noglob", "xargs"];

    /// <summary>
    /// The subcommands of <paramref name="command"/>, or null when it cannot be split with any
    /// confidence — an operator with nothing after it — which no allow rule can then approve.
    /// </summary>
    internal static IReadOnlyList<Subcommand>? Of(string command, out bool substitutes)
    {
        var pieces = new List<string>();
        substitutes = false;

        foreach (string inner in Substitutions(command))
        {
            substitutes = true;
            pieces.AddRange(Split(inner, out _));
        }

        pieces.AddRange(Split(command, out bool dangling));

        return dangling ? null : [.. pieces.Select(Forms)];
    }

    /// <summary>
    /// Every subcommand of <paramref name="command"/> in every form, for a deny or ask rule, which
    /// has to be tried against all of them whether or not the command splits cleanly.
    /// </summary>
    internal static IEnumerable<string> AllForms(string command)
    {
        foreach (string inner in Substitutions(command))
        {
            foreach (string piece in Split(inner, out _))
            {
                foreach (string form in Forms(piece).Forms)
                {
                    yield return form;
                }
            }
        }

        foreach (string piece in Split(command, out _))
        {
            foreach (string form in Forms(piece).Forms)
            {
                yield return form;
            }
        }
    }

    private static List<string> Split(string command, out bool dangling)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        dangling = false;
        bool afterOperator = false;

        for (int at = 0; at < command.Length; at++)
        {
            char character = command[at];

            // 2>&1, <&3 and &> are redirections, not a command sent to the background.
            bool redirection = character == '&'
                && ((at > 0 && command[at - 1] is '>' or '<') || (at + 1 < command.Length && command[at + 1] == '>'));

            // A subshell, a group and a process substitution each hold commands of their own. Where
            // a parenthesis or brace is only text, splitting at it leaves an allow one more piece
            // to cover, which is a question rather than a command nobody approved.
            if (character is '(' or ')' or '{' or '}')
            {
                Flush();

                continue;
            }

            if (character is '&' or '|' or ';' or '\n' or '\r' && !redirection)
            {
                bool andOr = character is '&' or '|'
                    && at + 1 < command.Length && command[at + 1] == character;

                Flush();

                if (andOr)
                {
                    at++;
                }

                afterOperator = andOr;

                continue;
            }

            if (!char.IsWhiteSpace(character))
            {
                afterOperator = false;
            }

            current.Append(character);
        }

        Flush();

        // 'npm test &&' ends in an operator with no command after it. Claude Code reads that as
        // a command it cannot parse, and so does this.
        dangling = afterOperator;

        return pieces;

        void Flush()
        {
            string piece = DenyRule.Normalize(current.ToString());

            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }

            current.Clear();
        }
    }

    /// <summary>What is inside each <c>$(…)</c> and each pair of backticks.</summary>
    private static IEnumerable<string> Substitutions(string command)
    {
        for (int at = 0; at < command.Length; at++)
        {
            if (command[at] == '`')
            {
                int close = command.IndexOf('`', at + 1);

                if (close < 0)
                {
                    yield return command[(at + 1)..];

                    yield break;
                }

                yield return command[(at + 1)..close];
                at = close;
            }
            else if (command[at] is '$' or '<' or '>' && at + 1 < command.Length && command[at + 1] == '(')
            {
                int depth = 0;
                int end = at + 1;

                for (; end < command.Length; end++)
                {
                    if (command[end] == '(')
                    {
                        depth++;
                    }
                    else if (command[end] == ')' && --depth == 0)
                    {
                        break;
                    }
                }

                string inner = command[(at + 2)..Math.Min(end, command.Length)];

                yield return inner;

                // A substitution inside this one is found by looking at what it holds.
                foreach (string nested in Substitutions(inner))
                {
                    yield return nested;
                }

                at = end;
            }
        }
    }

    private static Subcommand Forms(string piece)
    {
        string[] words = piece.Split(' ');
        int at = 0;

        // What starts a body — if true; then sudo ls — is not the command in it.
        while (at < words.Length - 1 && Keywords.Contains(words[at], StringComparer.Ordinal))
        {
            at++;
        }

        if (at > 0)
        {
            piece = string.Join(' ', words[at..]);
            words = words[at..];
            at = 0;
        }

        while (at < words.Length && IsAssignment(words[at]))
        {
            at++;
        }

        bool assigned = at > 0;
        int command = SkipWrappers(words, at);

        var forms = new List<string> { piece };

        if (at > 0 && at < words.Length)
        {
            forms.Add(string.Join(' ', words[at..]));
        }

        if (command > at && command < words.Length)
        {
            forms.Add(string.Join(' ', words[command..]));
        }

        // An allow rule approves a command, not the file it writes or reads: Claude Code checks a
        // redirection's target, and tee's, against its file rules, which are not here to consult.
        string? plain = assigned || Redirects(piece)
            ? null
            : command < words.Length ? string.Join(' ', words[command..]) : piece;

        return new Subcommand(forms, plain);
    }

    /// <summary>
    /// Where the command a run of wrappers is wrapping starts. A wrapper's own options are taken
    /// off with it, and so is <c>timeout</c>'s duration; <c>xargs</c> only counts bare, since
    /// with options of its own it is doing more than running its argument.
    /// </summary>
    private static int SkipWrappers(string[] words, int at)
    {
        while (at < words.Length && Wrappers.Contains(words[at], StringComparer.Ordinal))
        {
            string wrapper = words[at];

            if (wrapper == "xargs" && at + 1 < words.Length && words[at + 1].StartsWith('-'))
            {
                return at;
            }

            // 'command -v' looks a command up rather than running it.
            if (wrapper == "command" && at + 1 < words.Length && words[at + 1] is "-v" or "-V")
            {
                return at;
            }

            at++;

            while (at < words.Length && words[at].StartsWith('-'))
            {
                // nice -n 10 and stdbuf -o L: an option with its value in the next word.
                at += words[at] is "-n" or "-o" or "-e" or "-i" or "-s" or "-k" && at + 1 < words.Length ? 2 : 1;
            }

            if (wrapper == "timeout" && at < words.Length)
            {
                at++;
            }
        }

        return at;
    }

    private static bool Redirects(string piece)
    {
        string bare = Descriptors().Replace(piece, string.Empty);

        return bare.Contains('>', StringComparison.Ordinal)
            || bare.Contains('<', StringComparison.Ordinal)
            || bare.Split(' ').Contains("tee", StringComparer.Ordinal);
    }

    /// <summary>Redirections that touch no file: 2&gt;&amp;1, &lt;&amp;3, and to or from /dev/null.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\d*[<>]&\d+|\d*>>?\s*/dev/null|<\s*/dev/null")]
    private static partial System.Text.RegularExpressions.Regex Descriptors();

    private static readonly string[] Keywords = ["if", "then", "else", "elif", "do", "while", "until", "!", "time"];

    private static bool IsAssignment(string word)
    {
        int equals = word.IndexOf('=', StringComparison.Ordinal);

        return equals > 0
            && (char.IsAsciiLetter(word[0]) || word[0] == '_')
            && word.AsSpan(0, equals).IndexOfAnyExcept(Name) < 0;
    }

    private static readonly System.Buffers.SearchValues<char> Name = System.Buffers.SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_");
}
