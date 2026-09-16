namespace TerminalFs.Core.Tests;

/// <summary>
/// Which shell a command is handed to. A command is written in the syntax of the shell the person
/// writing it uses, so running it under a different one fails in ways that look like the
/// command's fault.
/// </summary>
public sealed class ShellSpecTests
{
    [Fact]
    public void TheFlagWinsOverTheEnvironment()
    {
        ShellSpec shell = ShellSpec.Resolve("/usr/bin/fish");

        Assert.Equal("/usr/bin/fish", shell.File);
    }

    [Fact]
    public void WithoutAFlagOrShellVariableTheDefaultIsBinSh()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "there is no /bin/sh on Windows");

        string? saved = Environment.GetEnvironmentVariable("SHELL");

        try
        {
            Environment.SetEnvironmentVariable("SHELL", null);

            ShellSpec shell = ShellSpec.Resolve(null);

            Assert.Equal("/bin/sh", shell.File);
            Assert.Equal(["-c"], shell.Prefix);
            Assert.False(shell.Raw);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHELL", saved);
        }
    }

    /// <summary>
    /// cmd.exe takes its command line raw, because .NET escapes an embedded quote the way the C
    /// runtime wants and cmd reads that escape as something else entirely.
    /// </summary>
    [Fact]
    public void OnWindowsTheShellIsCmdWithDSC()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "cmd.exe is Windows's");

        ShellSpec shell = ShellSpec.Resolve(null);

        Assert.EndsWith("cmd.exe", shell.File, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/d", "/s", "/c"], shell.Prefix);
        Assert.True(shell.Raw);
    }
}
