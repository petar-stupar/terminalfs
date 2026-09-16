namespace TerminalFs.Internal.Mount;

/// <summary>What an external command did.</summary>
/// <param name="File">The command that was run.</param>
/// <param name="ExitCode">Its exit code, or -1 when it never ran or was killed.</param>
/// <param name="Output">Everything it wrote to standard output.</param>
/// <param name="Error">Everything it wrote to standard error, or why it could not be run.</param>
/// <param name="Missing">Whether the command itself is not installed.</param>
internal sealed record CommandResult(
    string File,
    int ExitCode,
    string Output,
    string Error,
    bool Missing = false)
{
    /// <summary>Whether it ran and succeeded.</summary>
    internal bool Ok => !Missing && ExitCode == 0;

    /// <summary>The most useful line it produced, for a message to the user.</summary>
    internal string Reason
    {
        get
        {
            string[] lines =
            [
                .. (Error + Output)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ];

            return lines.Length == 0 ? $"{File} exited {ExitCode}" : lines[^1];
        }
    }
}
