using System.Diagnostics;
using System.Text.Json;

namespace TerminalFs.Internal.Sessions;

/// <summary>
/// What a session's server says about itself once its tree is mounted. It is written by the
/// server and read by whoever stops or collects the session, which may be a different process
/// days later, so it carries everything needed to find the server and undo the mount.
/// </summary>
/// <param name="Id">The session.</param>
/// <param name="Pid">The server's process id.</param>
/// <param name="ProcessStarted">
/// When that process started. A pid alone is reused; the pair is not, so a record left by a
/// server that died never sends a stop to whatever process got its number next.
/// </param>
/// <param name="Port">The 9P port the server bound, which the mount names.</param>
/// <param name="MountPath">Where the tree is mounted.</param>
/// <param name="WorkingDirectory">The directory its commands run in.</param>
/// <param name="Created">When the session was mounted.</param>
internal sealed record SessionRecord(
    string Id,
    int Pid,
    DateTimeOffset ProcessStarted,
    int Port,
    string MountPath,
    string WorkingDirectory,
    DateTimeOffset Created)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// How far apart two readings of one process's start time may be. Linux derives it from
    /// clock ticks since boot, so the same process can read back a few milliseconds apart.
    /// </summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>A record describing this process.</summary>
    internal static SessionRecord ForThisProcess(string id, int port, string mountPath, string workingDirectory)
    {
        using Process self = Process.GetCurrentProcess();

        return new SessionRecord(
            id,
            Environment.ProcessId,
            self.StartTime.ToUniversalTime(),
            port,
            mountPath,
            workingDirectory,
            DateTimeOffset.UtcNow);
    }

    /// <summary>The record at <paramref name="path"/>, or null when there is none to read.</summary>
    /// <remarks>
    /// A record that does not parse is treated as absent rather than as an error: it is written
    /// by rename, so a torn one means somebody wrote it by hand, and refusing to start or stop a
    /// session over it would leave the user with no command that clears it.
    /// </remarks>
    internal static SessionRecord? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path), Options);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes this record to <paramref name="path"/> whole, so a reader polling for it never sees
    /// half of one.
    /// </summary>
    internal void Write(string path)
    {
        string temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Whether the server this record describes is still running.</summary>
    internal bool ServerIsAlive()
    {
        try
        {
            using Process process = Process.GetProcessById(Pid);

            return !process.HasExited
                && (process.StartTime.ToUniversalTime() - ProcessStarted).Duration() <= StartTolerance;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
