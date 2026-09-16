using Microsoft.Extensions.Time.Testing;
using TerminalFs.Core.Permissions;

namespace TerminalFs.Core.Tests;

/// <summary>
/// The deny list in force, and how it survives the settings file being edited underneath it.
/// The asymmetry is the point: a bad file at startup stops the server, a bad file afterwards
/// changes nothing.
/// </summary>
public sealed class SettingsWatcherTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "terminalfs-tests-" + Guid.NewGuid().ToString("N"));

    private readonly List<string> reported = [];

    public SettingsWatcherTests()
    {
        Directory.CreateDirectory(directory);
        Diagnostics.To(reported.Add);
    }

    public void Dispose()
    {
        Diagnostics.To(null);

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string Path_ => Path.Combine(directory, "settings.json");

    private void Write(params string[] entries) =>
        File.WriteAllText(
            Path_,
            $$"""{ "permissions": { "deny": [{{string.Join(", ", entries.Select(e => $"\"{e}\""))}}] } }""");

    /// <summary>
    /// The file is the server's licence to start. Without one there are no rules, and a server
    /// running commands under rules nobody wrote is worse than a server that did not start.
    /// </summary>
    [Fact]
    public void AMissingFileRefusesTheInitialLoad()
    {
        CommandException refused = Assert.Throws<CommandException>(
            () => SettingsWatcher.Start(Path_, Interval, new FakeTimeProvider()));

        Assert.Equal(CommandErrno.NotFound, refused.Errno);
    }

    [Fact]
    public void AMalformedFileRefusesTheInitialLoad()
    {
        File.WriteAllText(Path_, "{ broken");

        Assert.Throws<CommandException>(
            () => SettingsWatcher.Start(Path_, Interval, new FakeTimeProvider()));
    }

    [Fact]
    public void AnEntryThatWouldNeverMatchRefusesTheInitialLoad()
    {
        File.WriteAllText(Path_, """{ "permissions": { "deny": ["Bash(ls) x"] } }""");

        Assert.Throws<CommandException>(
            () => SettingsWatcher.Start(Path_, Interval, new FakeTimeProvider()));
    }

    [Fact]
    public void AFileWithNoDenySectionIsValidAndRefusesNothing()
    {
        File.WriteAllText(Path_, "{}");

        using SettingsWatcher watcher = SettingsWatcher.Start(Path_, Interval, new FakeTimeProvider());

        Assert.Equal(0, watcher.Current.Count);
    }

    [Fact]
    public void AnEditIsPickedUpWithinThePollInterval()
    {
        Write("Bash(sudo:*)");

        var time = new FakeTimeProvider();
        using SettingsWatcher watcher = SettingsWatcher.Start(Path_, Interval, time);

        Assert.Null(watcher.Current.Match("git push --force"));

        Write("Bash(sudo:*)", "Bash(git push --force:*)");
        time.Advance(Interval);

        Assert.NotNull(watcher.Current.Match("git push --force"));
        Assert.Equal(2, watcher.Current.Count);
    }

    /// <summary>
    /// An editor that truncates before it writes leaves a window in which the file is empty and
    /// everything would be permitted. That is the one outcome a deny list must never have, so a
    /// reload that fails keeps what it had.
    /// </summary>
    [Fact]
    public void AMalformedEditKeepsTheLastGoodRules()
    {
        Write("Bash(sudo:*)");

        var time = new FakeTimeProvider();
        using SettingsWatcher watcher = SettingsWatcher.Start(Path_, Interval, time);
        DateTimeOffset loaded = watcher.LoadedAt;

        File.WriteAllText(Path_, "{ broken");
        time.Advance(Interval);

        Assert.NotNull(watcher.Current.Match("sudo ls"));
        Assert.Equal(loaded, watcher.LoadedAt);
        Assert.Contains(reported, line => line.Contains("keeping the 1 deny rule", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeletedFileKeepsTheLastGoodRules()
    {
        Write("Bash(sudo:*)");

        var time = new FakeTimeProvider();
        using SettingsWatcher watcher = SettingsWatcher.Start(Path_, Interval, time);

        File.Delete(Path_);
        time.Advance(Interval);

        Assert.NotNull(watcher.Current.Match("sudo ls"));
        Assert.Contains(reported, line => line.Contains("keeping the", StringComparison.Ordinal));
    }

    /// <summary>
    /// A file mid-write fixes itself a moment later, so the failed poll must not record the stamp
    /// it could not read — otherwise the recovery is never noticed.
    /// </summary>
    [Fact]
    public void AFileThatComesBackIsPickedUpAtTheNextPoll()
    {
        Write("Bash(sudo:*)");

        var time = new FakeTimeProvider();
        using SettingsWatcher watcher = SettingsWatcher.Start(Path_, Interval, time);

        File.WriteAllText(Path_, "{ broken");
        time.Advance(Interval);

        Write("Bash(halt)");
        time.Advance(Interval);

        Assert.Null(watcher.Current.Match("sudo ls"));
        Assert.NotNull(watcher.Current.Match("halt"));
    }

    [Fact]
    public void NothingIsRereadWhenTheFileHasNotMoved()
    {
        Write("Bash(sudo:*)");

        var time = new FakeTimeProvider();
        using SettingsWatcher watcher = SettingsWatcher.Start(Path_, Interval, time);

        reported.Clear();
        time.Advance(Interval);
        time.Advance(Interval);

        Assert.Empty(reported);
    }

    [Fact]
    public void InitSettingsWritesTheDefaultsAndRefusesToOverwrite()
    {
        Settings.WriteDefaults(Path_);

        using SettingsWatcher watcher = SettingsWatcher.Start(Path_, Interval, new FakeTimeProvider());

        Assert.Equal(Settings.Defaults.Count, watcher.Current.Count);
        Assert.NotNull(watcher.Current.Match("sudo ls"));

        CommandException refused = Assert.Throws<CommandException>(() => Settings.WriteDefaults(Path_));

        Assert.Equal(CommandErrno.Exists, refused.Errno);
    }

    /// <summary>The directory a fresh file needs is made, not demanded.</summary>
    [Fact]
    public void InitSettingsCreatesTheDirectoryItNeeds()
    {
        string nested = Path.Combine(directory, "a", "b", "settings.json");

        Settings.WriteDefaults(nested);

        Assert.True(File.Exists(nested));
    }
}
