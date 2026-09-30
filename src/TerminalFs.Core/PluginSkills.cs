using TerminalFs.Core.Internal.Nodes;

namespace TerminalFs.Core;

/// <summary>
/// The skills the agent plugins carry, as the tree serves them when nobody has said where it is.
/// </summary>
/// <remarks>
/// A plugin is loaded before any session's tree exists, since the plugin is what starts it, so it
/// cannot read its skill off a mount. It takes the skill from this program instead, and the
/// session's own path reaches the agent through the session context.
/// </remarks>
public static class PluginSkills
{
    /// <summary>The Claude Code skill, with <c>&lt;mount&gt;</c> left in.</summary>
    public static string ClaudeCode => TreeText.Skill(SkillHarness.ClaudeCode, mountPath: null);

    /// <summary>The opencode skill, with <c>&lt;mount&gt;</c> left in.</summary>
    public static string Opencode => TreeText.Skill(SkillHarness.OpenCode, mountPath: null);
}
