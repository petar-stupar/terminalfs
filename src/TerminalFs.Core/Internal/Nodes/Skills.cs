namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/skills</c>: how an agent should use this tree, one skill per harness.
/// </summary>
/// <remarks>
/// <para>
/// Served rather than shipped separately so that a mount carries its own instructions, and so
/// that what a harness is told matches the server it is talking to rather than whatever version
/// of a skill file happened to be copied around.
/// </para>
/// <para>
/// One skill per harness rather than one for all of them, because the cheap way to run a command
/// depends on the tools a harness has, and a skill that describes every harness's way is longer
/// and followed worse. Each lives at <c>/skills/&lt;harness&gt;/terminalfs/SKILL.md</c>, so
/// <c>/skills/&lt;harness&gt;</c> is a directory a harness can be pointed at whole.
/// </para>
/// </remarks>
internal static class Skills
{
    internal static TerminalDirectory Directory(DateTimeOffset builtAt, string? mountPath) =>
        new StaticDirectory(
            "skills",
            TerminalNodeKind.Skill,
            "/skills",
            [
                new TextPage("index.md", TerminalNodeKind.Skill, "/skills/index.md", () => TreeText.SkillsIndex(builtAt)),
                .. SkillHarness.All.Select(harness => For(harness, builtAt, mountPath)),
            ]);

    private static StaticDirectory For(SkillHarness harness, DateTimeOffset builtAt, string? mountPath)
    {
        string at = "/skills/" + harness.Directory;

        return new StaticDirectory(
            harness.Directory,
            TerminalNodeKind.Skill,
            at,
            [
                new TextPage(
                    "index.md",
                    TerminalNodeKind.Skill,
                    at + "/index.md",
                    () => TreeText.HarnessIndex(harness, builtAt)),
                new StaticDirectory(
                    "terminalfs",
                    TerminalNodeKind.Skill,
                    at + "/terminalfs",
                    [
                        new TextPage(
                            "index.md",
                            TerminalNodeKind.Skill,
                            at + "/terminalfs/index.md",
                            () => TreeText.SkillIndex(harness, builtAt, mountPath)),
                        new TextPage(
                            "SKILL.md",
                            TerminalNodeKind.Skill,
                            at + "/terminalfs/SKILL.md",
                            () => TreeText.Skill(harness, mountPath)),
                    ]),
            ]);
    }
}
