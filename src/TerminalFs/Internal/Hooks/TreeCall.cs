using System.Text.RegularExpressions;
using TerminalFs.Core;
using TerminalFs.Internal.Sessions;

namespace TerminalFs.Internal.Hooks;

/// <summary>What a tool call does to the session trees.</summary>
internal abstract record TreeCall
{
    /// <summary>Nothing: it does not touch the directory sessions live in.</summary>
    internal sealed record Elsewhere : TreeCall;

    /// <summary>It writes <paramref name="Command"/> to this session's <c>ctl/<paramref name="Name"/></c>.</summary>
    internal sealed record Run(string Name, string Command) : TreeCall;

    /// <summary>It only reads this session's tree, or ends one of its commands, and does nothing else.</summary>
    internal sealed record Harmless : TreeCall;

    /// <summary>It touches the tree in a way that is refused, for <paramref name="Reason"/>.</summary>
    internal sealed record Refused(string Reason) : TreeCall;

    /// <summary>
    /// It touches this session's tree only to read it, alongside other things that are the
    /// harness's own business.
    /// </summary>
    internal sealed record Mixed : TreeCall;
}

/// <summary>
/// Reads a tool call for what it does to the directory session trees live in.
/// </summary>
/// <remarks>
/// <para>
/// A command reaches a tree by being written to <c>ctl/&lt;name&gt;</c>, and the only writes this
/// accepts are ones it can read the command back out of: the <c>Write</c> tool's content, or the
/// Bash call the Claude Code skill spells out —
/// </para>
/// <code>
/// cat &gt; &lt;tree&gt;/ctl/build &lt;&lt;'CMD'
/// dotnet build 2&gt;&amp;1 | tail -40
/// CMD
/// cat &lt;tree&gt;/cmd/build/wait; cat &lt;tree&gt;/cmd/build/stdout
/// </code>
/// <para>
/// — with nothing after the heredoc but reads of the tree. Anything else that names <c>ctl</c>,
/// or another session's tree, or the directory by a spelling other than its own, is refused: a
/// command this cannot read is one it cannot check, and letting it through would make the check a
/// matter of phrasing.
/// </para>
/// <para>
/// None of this is a boundary. The agent runs as the same user as the server, and a path built at
/// run time, a symbolic link, or a <c>cd</c> followed by a relative path all reach the tree without
/// naming it here. It keeps an agent that is following its instructions inside its rules.
/// </para>
/// </remarks>
internal static partial class TreeCalls
{
    private static readonly System.Buffers.SearchValues<char> PathEnds =
        System.Buffers.SearchValues.Create(" \t\n\r;|&<>()`\"'");

    private static readonly System.Buffers.SearchValues<char> Expanded = System.Buffers.SearchValues.Create("$~`*?[");

    private static readonly System.Buffers.SearchValues<char> NotInARead = System.Buffers.SearchValues.Create("><`$&(){}\r\n");

    private static readonly string[] ReadingPrograms = ["cat", "ls", "tail", "head", "wc", "grep", "stat"];

    /// <summary>What a <c>Bash</c> call does.</summary>
    /// <param name="command">The command it runs.</param>
    /// <param name="paths">Where sessions live.</param>
    /// <param name="id">This session.</param>
    /// <param name="spellings">Other ways of writing <see cref="SessionPaths.Root"/>: through a variable, or <c>~</c>.</param>
    internal static TreeCall Bash(string command, SessionPaths paths, string id, IReadOnlyList<string> spellings)
    {
        // Quotes taken out first, so "$XDG_RUNTIME_DIR"/terminalfs is the spelling it stands for.
        string unquoted = command.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);
        string[] lines = command.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (spellings.FirstOrDefault(spelling => unquoted.Contains(spelling, StringComparison.Ordinal)) is { } other)
        {
            return new TreeCall.Refused(
                $"it names the session trees as '{other}'. Write the path out as {paths.Root}, so the command "
                + "written to the tree can be checked against your permission rules");
        }

        if (!Paths(command, paths.Root).Any())
        {
            // The skill's shape is only ever used for a tree, so one aimed at a path this cannot
            // see — relative, or built at run time — is a tree reached some other way.
            return Shape().Match(lines[0].Trim()) is { Success: true } elsewhere
                && Opaque(Unquoted(elsewhere.Groups["path"].Value))
                    ? new TreeCall.Refused(
                        $"it writes a command to {elsewhere.Groups["path"].Value}, which is not a path this check can "
                        + $"read. Write the full path, as the terminalfs skill does: cat > {paths.MountPath(id)}/ctl/<name> <<'CMD'")
                    : new TreeCall.Elsewhere();
        }

        string own = paths.MountPath(id);

        if (Shape().Match(lines[0].Trim()) is { Success: true } opening
            && Under(Normalised(Unquoted(opening.Groups["path"].Value)), paths.Root))
        {
            return Written(lines, Normalised(Unquoted(opening.Groups["path"].Value)), own, paths);
        }

        foreach (string path in Paths(command, paths.Root))
        {
            if (Check(path, own, paths, write: false) is { } refusal)
            {
                return refusal;
            }
        }

        return Segments(command).All(segment => IsRead(segment) || IsKill(segment, own))
            ? new TreeCall.Harmless()
            : new TreeCall.Mixed();
    }

    /// <summary>
    /// The sessions' directory written through the variables it came from, or <c>~</c>. A command
    /// spelled that way reaches the tree without naming it, so it is refused rather than missed.
    /// </summary>
    internal static List<string> Spellings(SessionPaths paths, Func<string, string?> environment, string home)
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

    /// <summary>What a <c>Write</c> call does.</summary>
    internal static TreeCall Write(string filePath, string content, SessionPaths paths, string id)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new TreeCall.Elsewhere();
        }

        string full = Path.GetFullPath(filePath);

        if (!Under(full, paths.Root))
        {
            return new TreeCall.Elsewhere();
        }

        string own = paths.MountPath(id);

        if (Check(full, own, paths, write: true) is { } refusal)
        {
            return refusal;
        }

        if (ControlName(full, own) is { } name)
        {
            return new TreeCall.Run(name, content);
        }

        return IsKillPath(full, own)
            ? new TreeCall.Harmless()
            : new TreeCall.Refused(
                $"{full} is not a file a command is written to. Write one to {own}/ctl/<name>");
    }

    /// <summary>What an <c>Edit</c>, or any other tool that changes a file in place, does.</summary>
    internal static TreeCall Edit(string filePath, SessionPaths paths) =>
        !string.IsNullOrWhiteSpace(filePath) && Under(Path.GetFullPath(filePath), paths.Root)
            ? new TreeCall.Refused(
                "nothing in a session tree is edited in place. Run a command with the Bash shape the "
                + "terminalfs skill gives")
            : new TreeCall.Elsewhere();

    private static TreeCall Written(string[] lines, string path, string own, SessionPaths paths)
    {
        if (Check(path, own, paths, write: true) is { } refusal)
        {
            return refusal;
        }

        if (ControlName(path, own) is not { } name)
        {
            return new TreeCall.Refused($"{path} is not a file a command is written to. Write one to {own}/ctl/<name>");
        }

        int end = Array.FindIndex(lines, 1, line => line.TrimEnd('\r') == "CMD");

        if (end < 0)
        {
            return new TreeCall.Refused("the heredoc is never closed: it needs a line that is only CMD");
        }

        // After the heredoc, reads of the tree and nothing else: the approval this call gets is for
        // the command inside it, and must not carry anything else through with it.
        foreach (string line in lines[(end + 1)..])
        {
            foreach (string segment in Segments(line))
            {
                bool reads = IsRead(segment)
                    && Paths(segment, paths.Root).All(read => Check(read, own, paths, write: false) is null);

                if (!reads)
                {
                    return new TreeCall.Refused(
                        $"'{segment}' follows the command in the same call. Only reads under {own}/cmd/ "
                        + "may; run anything else on its own");
                }
            }
        }

        string written = string.Join('\n', lines[1..end]);

        // A command that itself writes into a tree would carry a second command past the check,
        // and one that reaches another session's tree is what a tree per session is there to stop.
        foreach (string mentioned in Paths(written, paths.Root))
        {
            if (Check(mentioned, own, paths, write: false) is { } inner)
            {
                return inner;
            }
        }

        return new TreeCall.Run(name, written);
    }

    /// <summary>
    /// Why <paramref name="path"/>, somewhere under the sessions' directory, is refused, or null
    /// when it is somewhere in this session's own tree that is read, or written, as the skill says.
    /// </summary>
    private static TreeCall.Refused? Check(string path, string own, SessionPaths paths, bool write)
    {
        if (HasDotSegment(path))
        {
            return new TreeCall.Refused($"{path} climbs with '.' or '..'; name the file in the tree directly");
        }

        if (path == own)
        {
            return new TreeCall.Refused(
                $"it names this session's tree, {own}, without a file in it: through a variable, a cd or a relative "
                + "path, the command written to it cannot be read. Write the full path, as the terminalfs skill does: "
                + $"cat > {own}/ctl/<name> <<'CMD'");
        }

        if (!Under(path, own))
        {
            string first = path.Length > paths.Root.Length ? FirstSegment(path[(paths.Root.Length + 1)..]) : string.Empty;

            return first.Length > 0 && first != Path.GetFileName(own) && SessionPaths.IsValidId(first)
                ? new TreeCall.Refused($"{path} is in another session's tree; this session uses only its own, {own}")
                : new TreeCall.Refused(
                    $"{path} is not in this session's tree. Its commands go to {own}/ctl/<name>, and what they did is under {own}/cmd/");
        }

        if (write)
        {
            return null;
        }

        string inside = path[(own.Length + 1)..];

        return FirstSegment(inside) is "cmd" or "skills" or "index.md" || inside == "ctl/index.md"
            ? null
            : new TreeCall.Refused(
                $"{path} is reached in a way this check cannot read the command from. Write a command "
                + "with the Bash shape the terminalfs skill gives, and read what it did under cmd/");
    }

    private static string? ControlName(string path, string own)
    {
        string control = own + "/ctl/";

        return path.StartsWith(control, StringComparison.Ordinal) && CommandId.IsValid(path[control.Length..])
            ? path[control.Length..]
            : null;
    }

    private static bool IsKillPath(string path, string own) =>
        path.StartsWith(own + "/cmd/", StringComparison.Ordinal)
        && path.EndsWith("/kill", StringComparison.Ordinal)
        && CommandId.IsValid(path[(own.Length + "/cmd/".Length)..^"/kill".Length]);

    /// <summary>
    /// Reads that change nothing: a reading program, with no redirection, no substitution, and
    /// nothing that could start a command of its own inside it.
    /// </summary>
    private static bool IsRead(string segment) =>
        segment.AsSpan().IndexOfAny(NotInARead) < 0
        && ReadingPrograms.Contains(segment.Split(' ', '\t')[0], StringComparer.Ordinal);

    /// <summary><c>echo x &gt; &lt;tree&gt;/cmd/&lt;name&gt;/kill</c>, which ends a command.</summary>
    private static bool IsKill(string segment, string own) =>
        Kill().Match(segment) is { Success: true } kill && IsKillPath(Unquoted(kill.Groups["path"].Value), own);

    private static IEnumerable<string> Segments(string command) =>
        Separators().Split(command).Select(segment => segment.Trim()).Where(segment => segment.Length > 0);

    /// <summary>Every path in <paramref name="command"/> that starts with <paramref name="root"/>.</summary>
    private static IEnumerable<string> Paths(string command, string root)
    {
        for (int at = command.IndexOf(root, StringComparison.Ordinal); at >= 0; at = command.IndexOf(root, at + 1, StringComparison.Ordinal))
        {
            int end = command.AsSpan(at).IndexOfAny(PathEnds);

            string path = Normalised(end < 0 ? command[at..] : command.Substring(at, end));

            // A sibling that only starts with the same letters — terminalfs-old — is not a tree.
            if (Under(path, root))
            {
                yield return path;
            }
        }
    }

    /// <summary>
    /// A path whose target cannot be known from the text: relative, or with anything in it a shell
    /// would expand.
    /// </summary>
    private static bool Opaque(string path) =>
        !path.StartsWith('/') || path.AsSpan().IndexOfAny(Expanded) >= 0;

    private static string Normalised(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    private static string Unquoted(string path) =>
        path.Length >= 2 && path[0] == path[^1] && path[0] is '"' or '\'' ? path[1..^1] : path;

    private static bool Under(string path, string directory) =>
        path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static bool HasDotSegment(string path) =>
        path.Split('/').Any(segment => segment is "." or "..");

    private static string FirstSegment(string relative)
    {
        int slash = relative.IndexOf('/', StringComparison.Ordinal);

        return slash < 0 ? relative : relative[..slash];
    }

    [GeneratedRegex("""^cat\s*>\s*(?<path>"[^"]*"|'[^']*'|[^\s"'<>]+)\s*<<\s*'CMD'$""")]
    private static partial Regex Shape();

    [GeneratedRegex("""^echo\s+\S+\s*>\s*(?<path>"[^"]*"|'[^']*'|\S+)$""")]
    private static partial Regex Kill();

    [GeneratedRegex(@"&&|\|\||\|&|;|\||&|\n")]
    private static partial Regex Separators();
}
