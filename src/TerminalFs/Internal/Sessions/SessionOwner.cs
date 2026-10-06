using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TerminalFs.Internal.Sessions;

/// <summary>
/// The agent process a session's tree was last started or resumed for. Only that process ending
/// stops the tree from a hook.
/// </summary>
/// <remarks>
/// <para>
/// A session id is not a process. An agent harness that restarts — an editor reloading its
/// extension host — resumes the same session in a new process while the old one is still on its
/// way out, and the old one's end hook names the same id. Stopping on that tore the tree out from
/// under the process that had just resumed it, minutes into its work.
/// </para>
/// <para>
/// The pid is paired with its start time for the reason a record's is: a pid is reused, the pair
/// is not.
/// </para>
/// </remarks>
/// <param name="Pid">The agent's process id.</param>
/// <param name="Started">When that process started.</param>
/// <param name="Command">What it was running, for people reading the history.</param>
internal sealed record SessionOwner(int Pid, DateTimeOffset Started, string? Command = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>How far apart two readings of one process's start time may be.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Programs a harness may run a hook through, which are never the agent itself.
    /// </summary>
    private static readonly HashSet<string> Shells = new(StringComparer.Ordinal)
    {
        "sh", "bash", "dash", "zsh", "ash", "busybox", "env", "setsid", "timeout",
    };

    /// <summary>
    /// The agent that ran this process: the nearest ancestor that is not a shell. Null where the
    /// process table cannot say, which leaves a session unowned and stopped as it always was.
    /// </summary>
    internal static SessionOwner? OfCaller()
    {
        int? pid = ProcessTable.ParentOf(Environment.ProcessId);

        for (int depth = 0; pid is int candidate && candidate > 1 && depth < 8; depth++)
        {
            string? command = ProcessTable.CommandLine(candidate);
            string program = command is null ? string.Empty : Path.GetFileName(command.Split(' ', 2)[0]);

            if (!Shells.Contains(program))
            {
                return ProcessTable.Find(candidate) is { Zombie: false } entry
                    ? new SessionOwner(candidate, entry.Started, command)
                    : null;
            }

            pid = ProcessTable.ParentOf(candidate);
        }

        return null;
    }

    /// <summary>Whether this is the same process as <paramref name="other"/>.</summary>
    internal bool IsSameProcess(SessionOwner other) =>
        Pid == other.Pid && (Started - other.Started).Duration() <= StartTolerance;

    /// <summary>Whether this process is still running.</summary>
    internal bool IsAlive() =>
        ProcessTable.Find(Pid) is { Zombie: false } entry && (entry.Started - Started).Duration() <= StartTolerance;

    /// <summary>How it is passed on a command line: <c>pid:start</c>, the start in Unix milliseconds.</summary>
    internal string Argument() =>
        $"{Pid.ToString(CultureInfo.InvariantCulture)}:{Started.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Reads <see cref="Argument"/> back, or null when it is not one.</summary>
    internal static SessionOwner? Parse(string text)
    {
        string[] parts = text.Split(':');

        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            && pid > 1
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long started)
            && started is > 0 and < 253402300800000
                ? new SessionOwner(pid, DateTimeOffset.FromUnixTimeMilliseconds(started))
                : null;
    }

    /// <summary>The owner written at <paramref name="path"/>, or null when there is none to read.</summary>
    internal static SessionOwner? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionOwner>(File.ReadAllText(path), Options) is { Pid: > 1 } owner
                && owner.Started != default
                    ? owner
                    : null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes it to <paramref name="path"/> whole, readable by this user only.</summary>
    internal void Write(string path)
    {
        string temporary = path + ".tmp";

        using (FileStream stream = SessionFiles.CreatePrivate(temporary))
        {
            JsonSerializer.Serialize(stream, this, Options);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>One line saying which process this is.</summary>
    public override string ToString() =>
        $"pid {Pid.ToString(CultureInfo.InvariantCulture)}" + (Command is { Length: > 0 } command ? $" ({command})" : string.Empty);

    /// <summary>This process and the ones above it, for saying who asked for something.</summary>
    internal static string Ancestry()
    {
        var links = new List<string>();
        int? pid = Environment.ProcessId;

        for (int depth = 0; pid is int current && current > 0 && depth < 4; depth++)
        {
            links.Add($"{current.ToString(CultureInfo.InvariantCulture)} {ProcessTable.CommandLine(current) ?? "?"}");
            pid = current == 1 ? null : ProcessTable.ParentOf(current);
        }

        return string.Join(" < ", links);
    }

}
