using System.Diagnostics;

namespace TerminalFs.Internal.Sessions;

/// <summary>What the process table says about one pid.</summary>
/// <param name="Started">When the process with that pid started.</param>
/// <param name="Zombie">
/// Whether it has exited and is waiting to be reaped. A zombie still holds its pid and its process
/// group, and <c>kill(pid, 0)</c> — which is how <see cref="Process.HasExited"/> answers for a
/// process this one did not start — still succeeds on it.
/// </param>
internal readonly record struct ProcessEntry(DateTimeOffset Started, bool Zombie);

/// <summary>Asks the operating system about processes this one did not start.</summary>
internal static class ProcessTable
{
    /// <summary>
    /// The entry for <paramref name="pid"/>, or null when no process has it. Pids 0 and 1 are
    /// never anybody's server, and signalling either as one would reach the caller's own process
    /// group or init, so they are reported as absent.
    /// </summary>
    internal static ProcessEntry? Find(int pid)
    {
        if (pid <= 1)
        {
            return null;
        }

        DateTimeOffset started;

        try
        {
            using Process process = Process.GetProcessById(pid);

            if (process.HasExited)
            {
                return null;
            }

            started = process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            // The last is the process leaving between being found and its start time being read,
            // which is the very thing a stop polls for.
            return null;
        }

        return new ProcessEntry(started, IsZombie(pid));
    }

    /// <summary>
    /// The processes in group <paramref name="group"/> whose environment has
    /// <paramref name="variable"/> set to <paramref name="value"/>. Only Linux says, through
    /// <c>/proc</c>; elsewhere there are none. A process whose environment this user cannot read
    /// — one run through <c>sudo</c> — is not among them, and could not be signalled anyway.
    /// </summary>
    internal static List<int> GroupMembersCarrying(int group, string variable, string value)
    {
        var members = new List<int>();

        if (!OperatingSystem.IsLinux() || group <= 1)
        {
            return members;
        }

        byte[] wanted = System.Text.Encoding.UTF8.GetBytes(variable + "=" + value);

        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), System.Globalization.CultureInfo.InvariantCulture, out int pid)
                || pid <= 1)
            {
                continue;
            }

            try
            {
                // The group is the fifth field, the third after the command name, which is in
                // parentheses and may contain spaces and parentheses itself.
                string stat = File.ReadAllText($"/proc/{pid}/stat");
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');

                if (fields.Length < 3
                    || !int.TryParse(fields[2], System.Globalization.CultureInfo.InvariantCulture, out int pgid)
                    || pgid != group)
                {
                    continue;
                }

                byte[] environment = File.ReadAllBytes($"/proc/{pid}/environ");

                foreach (Range entry in environment.AsSpan().Split((byte)0))
                {
                    if (environment.AsSpan()[entry].SequenceEqual(wanted))
                    {
                        members.Add(pid);
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Gone since the listing, or not ours to read.
            }
        }

        return members;
    }

    /// <summary>
    /// Whether <paramref name="pid"/> is a zombie. Only Linux says, through <c>/proc</c>; where it
    /// does not, a process is taken at its word, which is only wrong under a parent that never
    /// reaps.
    /// </summary>
    private static bool IsZombie(int pid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            // The command name is in parentheses and may contain anything, spaces and
            // parentheses included, so the state is found after the last ')'.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int close = stat.LastIndexOf(')');

            return close >= 0 && close + 2 < stat.Length && stat[close + 2] is 'Z' or 'X';
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Gone since the lookup above.
            return true;
        }
    }
}
