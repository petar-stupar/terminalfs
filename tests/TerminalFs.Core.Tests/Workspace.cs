namespace TerminalFs.Core.Tests;

/// <summary>
/// A registry with its own output directory, torn down with the test.
/// </summary>
/// <remarks>
/// Every suite here drives a real registry rather than a stand-in: what is being tested is
/// mostly the behaviour of processes, files and timers, and a fake of any of those would be
/// testing the fake.
/// </remarks>
internal sealed class Workspace : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "terminalfs-tests-" + Guid.NewGuid().ToString("N"));

    internal Workspace(CommandOptions? options = null)
    {
        Directory.CreateDirectory(root);

        Registry = CommandRegistry.Create((options ?? new CommandOptions()) with
        {
            OutputRoot = Path.Combine(root, "out"),
            WorkingDirectory = root,
        });
    }

    internal CommandRegistry Registry { get; }

    internal string Root => root;

    /// <summary>Reserves an id and starts it, as a write to the control file would.</summary>
    internal Command Run(string id, string text)
    {
        Command command = Registry.Reserve(id);

        Registry.Start(command, text);

        return command;
    }

    /// <summary>
    /// Waits for a command to stop. Bounded, because a test that hangs tells nobody anything.
    /// </summary>
    internal static async Task<CommandState> Finished(Command command) =>
        await command.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    internal static string Read(OutputFile file)
    {
        using Stream stream = file.OpenRead();
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    /// <summary>A command that runs long enough to be caught running, in either shell.</summary>
    internal static string Sleep(int seconds) => OperatingSystem.IsWindows()
        ? $"ping -n {seconds + 1} 127.0.0.1 >NUL"
        : $"sleep {seconds}";

    public void Dispose()
    {
        Registry.Dispose();

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
        }
    }
}
