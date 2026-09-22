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

    /// <param name="options">What the registry is told to do.</param>
    /// <param name="settle">
    /// How long a name waits before the command written to it runs. Zero unless a test says
    /// otherwise, because most of them are about something other than that window and a name
    /// that runs at its close is what every one of them meant before the window existed.
    /// </param>
    internal Workspace(CommandOptions? options = null, TimeSpan? settle = null)
    {
        Directory.CreateDirectory(root);

        Registry = CommandRegistry.Create((options ?? new CommandOptions()) with
        {
            OutputRoot = Path.Combine(root, "out"),
            WorkingDirectory = root,
            Settle = settle ?? TimeSpan.Zero,
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

    /// <summary>Takes a name and opens it, as a create followed by its open does.</summary>
    internal ControlSession Take(string id) =>
        Registry.OpenControl(Registry.CreateDraft(id), claiming: true);

    /// <summary>Writes one command to a name and closes it, as a shell redirect does.</summary>
    internal Draft Write(string id, string text)
    {
        using ControlSession session = Take(id);

        session.Write(System.Text.Encoding.UTF8.GetBytes(text));

        return session.Draft;
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

    /// <summary>
    /// Runs several commands one after another, in whichever shell this is.
    /// </summary>
    /// <remarks>
    /// The separator is not the same everywhere, and the difference is silent rather than loud.
    /// <c>;</c> separates for a POSIX shell and means nothing to <c>cmd</c>, which reads
    /// <c>echo out; echo err 1&gt;&amp;2</c> as one <c>echo</c> of the whole line — so the test
    /// that wanted a line on each stream got both on one and neither where it looked. <c>&amp;</c>
    /// separates for cmd and backgrounds for a POSIX shell, so neither spelling is portable and
    /// the choice has to be made here.
    /// </remarks>
    internal static string Then(params string[] commands) =>
        string.Join(OperatingSystem.IsWindows() ? " & " : "; ", commands);

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
