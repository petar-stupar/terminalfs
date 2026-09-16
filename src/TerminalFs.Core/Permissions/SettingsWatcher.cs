namespace TerminalFs.Core.Permissions;

/// <summary>
/// The deny list in force, kept in step with the settings file while the server runs.
/// </summary>
/// <remarks>
/// <para>
/// The first load is the server's licence to start: a settings file that is missing or malformed
/// at startup stops it, because the alternative is a server that runs commands under rules nobody
/// wrote. Every load after that is different — the server is already running, commands may be in
/// flight, and there is no caller to report to — so a failed reload <em>keeps the rules it has</em>
/// and says so on the diagnostics channel. An editor that writes through a truncate-then-write
/// would otherwise leave a window in which the file is empty and everything is permitted, which
/// is the one outcome a deny list must never have.
/// </para>
/// <para>
/// Polling rather than <c>FileSystemWatcher</c>. The file is small, two seconds is soon enough
/// for a rule change, and a watcher's behaviour differs across platforms and across the several
/// ways an editor can save — rename-over, truncate-in-place, write-to-temp-and-move — each of
/// which produces a different event, or none. Comparing size and write time answers the only
/// question being asked and answers it the same way everywhere.
/// </para>
/// </remarks>
public sealed class SettingsWatcher : IDisposable
{
    private readonly string path;
    private readonly TimeProvider time;
    private readonly ITimer timer;
    private readonly Lock gate = new();

    private DenyList current;
    private DateTimeOffset loadedAt;
    private (long Length, DateTimeOffset Written) seen;

    private SettingsWatcher(
        string path,
        DenyList first,
        (long, DateTimeOffset) stamp,
        TimeSpan interval,
        TimeProvider time)
    {
        this.path = path;
        this.time = time;
        current = first;
        seen = stamp;
        loadedAt = time.GetUtcNow();

        timer = time.CreateTimer(_ => Poll(), null, interval, interval);
    }

    /// <summary>Where the rules are read from.</summary>
    public string Path => path;

    /// <summary>The rules in force right now.</summary>
    /// <remarks>
    /// Read once per check and used for the whole of it. Taking the reference rather than
    /// consulting the watcher repeatedly is what makes a reload invisible to a check already
    /// under way.
    /// </remarks>
    public DenyList Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>When the rules in force were read.</summary>
    public DateTimeOffset LoadedAt
    {
        get
        {
            lock (gate)
            {
                return loadedAt;
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="path"/> and watches it. The read must succeed.
    /// </summary>
    /// <exception cref="CommandException">
    /// The file is missing, unreadable or malformed. The server does not start.
    /// </exception>
    public static SettingsWatcher Start(
        string path,
        TimeSpan interval,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        TimeProvider clock = time ?? TimeProvider.System;
        string full = System.IO.Path.GetFullPath(path);
        DenyList first = Settings.Load(full);

        return new SettingsWatcher(full, first, Stamp(full), interval, clock);
    }

    /// <summary>Re-reads the file now, whatever the timer is doing.</summary>
    /// <remarks>Exposed for the suite, which drives time rather than waiting for it.</remarks>
    internal void Poll()
    {
        (long Length, DateTimeOffset Written) stamp;

        try
        {
            stamp = Stamp(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Keep(exception.Message);
            return;
        }

        lock (gate)
        {
            if (stamp == seen)
            {
                return;
            }
        }

        DenyList reloaded;

        try
        {
            reloaded = Settings.Load(path);
        }
        catch (CommandException refused)
        {
            // The stamp is not recorded, so the next poll tries again. An editor caught
            // mid-write is the ordinary reason to be here, and it fixes itself a moment later.
            Keep(refused.Message);
            return;
        }

        lock (gate)
        {
            current = reloaded;
            loadedAt = time.GetUtcNow();
            seen = stamp;
        }

        Diagnostics.Report(
            $"settings: {path}: {Rules(reloaded.Count)} now in force");
    }

    /// <summary>Stops watching. The rules last loaded stay in <see cref="Current"/>.</summary>
    public void Dispose() => timer.Dispose();

    private void Keep(string reason)
    {
        int count;
        DateTimeOffset since;

        lock (gate)
        {
            count = current.Count;
            since = loadedAt;
        }

        Diagnostics.Report(
            $"settings: {path}: {reason}; keeping the {Rules(count)} loaded at "
                + since.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Rules(int count) => count == 1 ? "1 deny rule" : $"{count} deny rules";

    /// <summary>
    /// What is compared to decide whether the file moved. A file deleted between polls reads as
    /// a length of -1, which differs from any real file and so provokes a reload attempt — which
    /// fails, and keeps the rules.
    /// </summary>
    private static (long Length, DateTimeOffset Written) Stamp(string path)
    {
        var file = new FileInfo(path);

        return file.Exists
            ? (file.Length, file.LastWriteTimeUtc)
            : (-1, DateTimeOffset.MinValue);
    }
}
