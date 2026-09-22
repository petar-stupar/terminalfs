namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// <c>/skills</c>: how an agent should use this tree.
/// </summary>
/// <remarks>
/// Served rather than shipped separately so that a mount carries its own instructions, and so
/// that what a harness is told matches the server it is talking to rather than whatever version
/// of a skill file happened to be copied around.
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
                new StaticDirectory(
                    "terminalfs",
                    TerminalNodeKind.Skill,
                    "/skills/terminalfs",
                    [
                        new TextPage(
                            "index.md",
                            TerminalNodeKind.Skill,
                            "/skills/terminalfs/index.md",
                            () => TreeText.SkillIndex(builtAt, mountPath)),
                        new TextPage(
                            "SKILL.md",
                            TerminalNodeKind.Skill,
                            "/skills/terminalfs/SKILL.md",
                            () => TreeText.Skill(mountPath)),
                    ]),
            ]);
}
