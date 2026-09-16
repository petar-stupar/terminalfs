using System.Diagnostics;
using System.Text;

namespace TerminalFs.Internal.Mount;

/// <summary>
/// Runs an external command and collects what it said. Mounting is done by asking the operating
/// system's own tools rather than by linking against them, so this is the whole of the mechanism.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Runs <paramref name="file"/> with <paramref name="arguments"/> and waits for it. Standard
    /// output and standard error are captured rather than inherited: a mount tool's message is
    /// something this program reports in its own words, not noise printed past it.
    /// </summary>
    internal static async Task<CommandResult> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };

        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(error, e.Data);

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new CommandResult(file, -1, string.Empty, exception.Message, Missing: true);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killed first, then read. The reader threads append to these builders until the
            // process is gone, and reading one while it is being written to is not safe.
            Kill(process);
            process.WaitForExit();

            return new CommandResult(file, -1, output.ToString(), $"timed out after {timeout.TotalSeconds:0}s");
        }

        return new CommandResult(file, process.ExitCode, output.ToString(), error.ToString());
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // It exited between the timeout and the kill, which is the outcome the kill wanted.
        }
    }

    /// <summary>
    /// Whether <paramref name="file"/> can be run at all.
    /// </summary>
    /// <remarks>
    /// PATH is searched here rather than by shelling out to <c>which</c>, which is not present on
    /// a minimal image — and its absence was reported as "docker is not installed", which is a
    /// different problem and sends the reader somewhere else entirely.
    /// </remarks>
    internal static bool Exists(string file)
    {
        if (Path.IsPathRooted(file))
        {
            return File.Exists(file);
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [string.Empty];

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory, file + extension)))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not a reason to stop looking at the rest.
                }
            }
        }

        return false;
    }
}
