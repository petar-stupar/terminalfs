using System.Collections.Immutable;

namespace TerminalFs.Core.Permissions;

/// <summary>
/// The rules in force, and what they refuse.
/// </summary>
/// <remarks>
/// <para>
/// A list is immutable once built. Reloading the settings file builds a new one and swaps it in,
/// so a check that is already under way finishes against the rules it started with rather than
/// against a half-applied edit.
/// </para>
/// <para>
/// Every rule is applied to the command as a whole <em>and</em> to each of its segments. A shell
/// command is not one command: <c>cd /tmp &amp;&amp; sudo ls</c> runs two, and a rule that only
/// looked at the whole string would let the second one through behind the first. Splitting is
/// deliberately coarse — it is a refusal, not a parser, and the cost of splitting somewhere a
/// shell would not is a command refused that could have run, which a caller can see and reword.
/// The cost of the opposite is a command that should not have run.
/// </para>
/// </remarks>
public sealed class DenyList
{
    private static readonly char[] SegmentBreaks = ['&', '|', ';', '\n', '\r'];

    private readonly ImmutableArray<DenyRule> rules;

    private DenyList(ImmutableArray<DenyRule> rules) => this.rules = rules;

    /// <summary>A list that refuses nothing.</summary>
    public static DenyList Empty { get; } = new(ImmutableArray<DenyRule>.Empty);

    /// <summary>How many rules are in force.</summary>
    public int Count => rules.Length;

    /// <summary>The rules, in the order they were written.</summary>
    public IReadOnlyList<DenyRule> Rules => rules;

    /// <summary>
    /// Builds a list from the entries of a settings file, ignoring those that belong to another
    /// tool.
    /// </summary>
    /// <exception cref="CommandException">An entry is malformed.</exception>
    public static DenyList Of(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var parsed = ImmutableArray.CreateBuilder<DenyRule>();

        foreach (string entry in entries)
        {
            if (DenyRule.Parse(entry) is { } rule)
            {
                parsed.Add(rule);
            }
        }

        return new DenyList(parsed.ToImmutable());
    }

    /// <summary>
    /// The first rule that refuses <paramref name="command"/>, or null when none does.
    /// </summary>
    public DenyRule? Match(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (rules.Length == 0)
        {
            return null;
        }

        string whole = DenyRule.Normalize(command);

        foreach (DenyRule rule in rules)
        {
            if (rule.Matches(whole))
            {
                return rule;
            }
        }

        foreach (string segment in Segments(command))
        {
            foreach (DenyRule rule in rules)
            {
                if (rule.Matches(segment))
                {
                    return rule;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The command broken at the operators that start a new one: <c>&amp;&amp;</c>, <c>||</c>,
    /// <c>;</c>, <c>|</c>, <c>&amp;</c> and a newline. Each piece comes back normalised, and
    /// empty pieces are dropped.
    /// </summary>
    private static IEnumerable<string> Segments(string command)
    {
        int at = 0;

        while (at < command.Length)
        {
            int next = command.IndexOfAny(SegmentBreaks, at);

            if (next < 0)
            {
                string last = DenyRule.Normalize(command[at..]);

                if (last.Length > 0)
                {
                    yield return last;
                }

                yield break;
            }

            string piece = DenyRule.Normalize(command[at..next]);

            if (piece.Length > 0)
            {
                yield return piece;
            }

            // Step over the operator, however many characters it turned out to be.
            char operatorCharacter = command[next];
            at = next + 1;

            while (at < command.Length && command[at] == operatorCharacter)
            {
                at++;
            }
        }
    }
}
