namespace TerminalFs.Core;

/// <summary>
/// The shell a command is handed to, and how its text is handed over.
/// </summary>
/// <param name="File">The shell's executable.</param>
/// <param name="Prefix">The arguments that come before the command text.</param>
/// <param name="Raw">
/// Whether the text must be placed on the command line as written rather than through the
/// argument list. True only for <c>cmd.exe</c>; see the remarks.
/// </param>
/// <remarks>
/// <para>
/// Two shells, two ways of being spoken to. A POSIX shell takes <c>-c</c> and then one argument,
/// and .NET's <c>ArgumentList</c> quotes that argument correctly for the C runtime's rules, which
/// is what every shell on Unix follows.
/// </para>
/// <para>
/// <c>cmd.exe</c> does not follow those rules. It has its own, and .NET escapes an embedded quote
/// as <c>\"</c>, which cmd reads as a literal backslash followed by the end of a quoted run —
/// so <c>echo "a b"</c> handed through <c>ArgumentList</c> arrives at cmd as something else. The
/// only way to give cmd exactly what was written is to build the command line by hand and let
/// <c>/s</c> strip the one outer pair of quotes this adds. That is what <see cref="Raw"/> means,
/// and it is why it is a property of the shell rather than a setting.
/// </para>
/// </remarks>
public readonly record struct ShellSpec(string File, IReadOnlyList<string> Prefix, bool Raw)
{
    /// <summary>
    /// The shell to use: <paramref name="requested"/> if a flag named one, else the caller's own
    /// shell, else the one every Unix is required to have.
    /// </summary>
    /// <remarks>
    /// <c>$SHELL</c> is honoured because a command written by someone who uses fish or zsh is
    /// written in that shell's syntax, and running it under <c>sh</c> would fail in ways that
    /// look like the command's fault. On Windows the environment's <c>ComSpec</c> plays the same
    /// role, and there is no equivalent of a per-user login shell to consult.
    /// </remarks>
    public static ShellSpec Resolve(string? requested)
    {
        if (OperatingSystem.IsWindows())
        {
            string comspec = requested
                ?? Environment.GetEnvironmentVariable("ComSpec")
                ?? "cmd.exe";

            return new ShellSpec(comspec, ["/d", "/s", "/c"], Raw: true);
        }

        string shell = requested
            ?? Environment.GetEnvironmentVariable("SHELL")
            ?? "/bin/sh";

        return new ShellSpec(shell, ["-c"], Raw: false);
    }
}
