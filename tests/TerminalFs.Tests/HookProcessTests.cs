using System.Diagnostics;

namespace TerminalFs.Tests;

/// <summary>
/// The hook as Claude Code runs it: a process, its input on standard input, and an exit code that
/// means something. 2 blocks the call whatever was printed, and any other failure is reported and
/// then ignored, so a hook that crashes lets the call it was checking straight through.
/// </summary>
public sealed class HookProcessTests
{
    private static async Task<(int Exit, string Output)> RunAsync(string input, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "terminalfs.dll"));

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;

        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        _ = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "session_id": "s1", "tool_name": "Write", "tool_input": { "file_path": "", "content": "x" } }""")]
    [InlineData("""{ "tool_name": "Bash", "tool_input": { "command": "ls" } }""")]
    public async Task InputThatCannotBeCheckedIsAnsweredRatherThanCrashedOn(string input)
    {
        (int exit, string output) = await RunAsync(input, "hook", "claude", "pre-tool-use");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, output.Trim());
    }

    /// <summary>A call headed for a tree that cannot be read is refused, not waved through.</summary>
    [Fact]
    public async Task InputThatCannotBeCheckedAndNamesATreeIsRefused()
    {
        (int exit, string output) = await RunAsync(
            """{ "tool_name": "Bash", "tool_input": { "command": "cat > /run/user/1000/terminalfs/x/ctl/y" } }""",
            "hook", "claude", "pre-tool-use");

        Assert.Equal(0, exit);
        Assert.Contains("\"permissionDecision\":\"deny\"", output, StringComparison.Ordinal);
    }
}
