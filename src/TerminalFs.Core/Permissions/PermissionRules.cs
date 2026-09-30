using System.Collections.Immutable;
using TerminalFs.Core.Internal;

namespace TerminalFs.Core.Permissions;

/// <summary>What a rule says about a command.</summary>
public enum RuleKind
{
    /// <summary>Refuse it.</summary>
    Deny,

    /// <summary>Ask the person at the prompt first.</summary>
    Ask,

    /// <summary>Run it without asking.</summary>
    Allow,
}

/// <summary>The permission rules one settings file holds.</summary>
/// <param name="Origin">Where they came from, for naming in a refusal: usually the file's path.</param>
/// <param name="Allow">Its <c>permissions.allow</c> entries.</param>
/// <param name="Ask">Its <c>permissions.ask</c> entries.</param>
/// <param name="Deny">Its <c>permissions.deny</c> entries.</param>
public sealed record RuleSource(
    string Origin,
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Ask,
    IReadOnlyList<string> Deny);

/// <summary>The rule that decided a command, and where it was written.</summary>
/// <param name="Kind">What it said.</param>
/// <param name="Rule">The entry, as it was written.</param>
/// <param name="Origin">The <see cref="RuleSource.Origin"/> it came from.</param>
public sealed record RuleVerdict(RuleKind Kind, string Rule, string Origin);

/// <summary>
/// An agent harness's <c>Bash(...)</c> permission rules, merged from every file that holds them,
/// and what they say about a command.
/// </summary>
/// <remarks>
/// <para>
/// The order is Claude Code's: deny, then ask, then allow, whichever file each came from. A deny
/// anywhere cannot be allowed back by an allow anywhere else, and an ask cannot be skipped by a
/// narrower allow.
/// </para>
/// <para>
/// A deny or ask rule matches when it matches the whole command or any one of its subcommands,
/// including a command inside <c>$(…)</c> or backticks. An allow rule has to match every
/// subcommand, and never approves a command it cannot see all of: one with a substitution, or one
/// that ends in an operator. That asymmetry is the point — the cost of a deny that fires too often
/// is a question, and the cost of an allow that fires too often is a command nobody approved.
/// </para>
/// <para>
/// Entries for other tools are ignored, as are entries Claude Code would itself skip as
/// malformed. <c>Bash</c> on its own, or <c>Bash(*)</c>, matches every command.
/// </para>
/// </remarks>
public sealed class PermissionRules
{
    private readonly ImmutableArray<(DenyRule Rule, string Origin)> deny;
    private readonly ImmutableArray<(DenyRule Rule, string Origin)> ask;
    private readonly ImmutableArray<(DenyRule Rule, string Origin)> allow;

    private PermissionRules(
        ImmutableArray<(DenyRule, string)> deny,
        ImmutableArray<(DenyRule, string)> ask,
        ImmutableArray<(DenyRule, string)> allow)
    {
        this.deny = deny;
        this.ask = ask;
        this.allow = allow;
    }

    /// <summary>Rules that say nothing about anything.</summary>
    public static PermissionRules None { get; } = Of([]);

    /// <summary>How many <c>Bash</c> rules are in force, of every kind.</summary>
    public int Count => deny.Length + ask.Length + allow.Length;

    /// <summary>Merges the rules of <paramref name="sources"/>, in the order given.</summary>
    public static PermissionRules Of(IEnumerable<RuleSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var deny = ImmutableArray.CreateBuilder<(DenyRule, string)>();
        var ask = ImmutableArray.CreateBuilder<(DenyRule, string)>();
        var allow = ImmutableArray.CreateBuilder<(DenyRule, string)>();

        foreach (RuleSource source in sources)
        {
            Add(deny, source.Deny, source.Origin);
            Add(ask, source.Ask, source.Origin);
            Add(allow, source.Allow, source.Origin);
        }

        return new PermissionRules(deny.ToImmutable(), ask.ToImmutable(), allow.ToImmutable());
    }

    /// <summary>
    /// What the rules say about <paramref name="command"/>, or null when none of them says
    /// anything and the harness's permission mode decides.
    /// </summary>
    public RuleVerdict? Decide(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        string[] forms = [DenyRule.Normalize(command), .. Subcommands.AllForms(command)];

        if (FirstMatch(deny, forms) is { } denied)
        {
            return new RuleVerdict(RuleKind.Deny, denied.Rule.Text, denied.Origin);
        }

        if (FirstMatch(ask, forms) is { } asked)
        {
            return new RuleVerdict(RuleKind.Ask, asked.Rule.Text, asked.Origin);
        }

        IReadOnlyList<Subcommand>? subcommands = Subcommands.Of(command, out bool substitutes);

        if (subcommands is null || substitutes || subcommands.Count == 0 || allow.IsEmpty)
        {
            return null;
        }

        (DenyRule Rule, string Origin)? first = null;

        foreach (Subcommand subcommand in subcommands)
        {
            if (subcommand.Plain is null || FirstMatch(allow, [subcommand.Plain]) is not { } allowed)
            {
                return null;
            }

            first ??= allowed;
        }

        return first is { } rule ? new RuleVerdict(RuleKind.Allow, rule.Rule.Text, rule.Origin) : null;
    }

    private static (DenyRule Rule, string Origin)? FirstMatch(
        ImmutableArray<(DenyRule Rule, string Origin)> rules,
        IReadOnlyList<string> forms)
    {
        foreach ((DenyRule Rule, string Origin) entry in rules)
        {
            foreach (string form in forms)
            {
                if (entry.Rule.Matches(form))
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static void Add(
        ImmutableArray<(DenyRule, string)>.Builder rules,
        IReadOnlyList<string>? entries,
        string origin)
    {
        foreach (string entry in entries ?? [])
        {
            string trimmed = entry.Trim();

            if (trimmed is "Bash" or "Bash(*)")
            {
                rules.Add((DenyRule.Everything(trimmed), origin));

                continue;
            }

            DenyRule? rule;

            try
            {
                rule = DenyRule.Parse(entry);
            }
            catch (CommandException)
            {
                // Claude Code skips an entry it cannot read and warns at startup; the warning is
                // its to give, and the rule is skipped here too so the two agree on what is in force.
                continue;
            }

            if (rule is not null)
            {
                rules.Add((rule, origin));
            }
        }
    }
}
