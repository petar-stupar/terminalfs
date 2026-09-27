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
    /// half of one. Readable by this user only: it names the port that runs commands as them.
    /// </summary>
    internal void Write(string path)
    {
        string temporary = path + ".tmp";

        using (FileStream stream = SessionFiles.CreatePrivate(temporary))
        {
            JsonSerializer.Serialize(stream, this, Options);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Whether this record can be acted on as session <paramref name="id"/>'s, mounted at
    /// <paramref name="mountPath"/>.
    /// </summary>
    /// <remarks>
    /// Everything that stops a session signals the pid and unmounts the path this names, so a
    /// record that parses is not enough. One from another version, one edited by hand, or one
    /// copied from another session could otherwise send a signal to pid 0 — the caller's own
    /// process group — or detach a mount that belongs to somebody else.
    /// </remarks>
    internal bool Describes(string id, string mountPath) =>
        Id == id
        && Pid > 1
        && Port is > 0 and < 65536
        && MountPath == mountPath
        && !string.IsNullOrEmpty(WorkingDirectory)
        && ProcessStarted != default
        && Created != default;

    /// <summary>Whether the server this record describes is still running.</summary>
    internal bool ServerIsAlive() => ProcessTable.Find(Pid) is { Zombie: false } entry && IsServer(entry);

    /// <summary>
    /// Whether the server this record describes has gone and cannot come back: no process has its
    /// pid, or one does and is a zombie of it. Either way, its process group holds nothing but the
    /// commands it left behind.
    /// </summary>
    /// <remarks>
    /// A pid that now belongs to a different process is neither: the record says nothing about
    /// that process or its group, and nothing is done to them.
    /// </remarks>
    internal bool ServerIsGone() => ProcessTable.Find(Pid) is not { } entry || (entry.Zombie && IsServer(entry));

    private bool IsServer(ProcessEntry entry) => (entry.Started - ProcessStarted).Duration() <= StartTolerance;
}
