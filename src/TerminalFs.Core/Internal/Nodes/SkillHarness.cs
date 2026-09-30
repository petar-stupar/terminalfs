namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// An agent harness this tree serves a skill for.
/// </summary>
/// <param name="Directory">The directory under <c>/skills</c> its skill is in.</param>
/// <param name="Title">What the harness is called, for the pages about it.</param>
internal sealed record SkillHarness(string Directory, string Title)
{
    /// <summary>opencode: one <c>execute</c> script writes the command and reads what it did.</summary>
    internal static SkillHarness OpenCode { get; } = new("opencode", "opencode");

    /// <summary>Claude Code: one Bash call writes the command and reads what it did.</summary>
    internal static SkillHarness ClaudeCode { get; } = new("claude-code", "Claude Code");

    /// <summary>Every harness, in the order <c>/skills</c> lists them.</summary>
    internal static IReadOnlyList<SkillHarness> All { get; } = [OpenCode, ClaudeCode];
}
