using System.ComponentModel;
using System.Diagnostics;

namespace TerminalFs.Core.Internal;

/// <summary>
/// Starts a command's process and follows it until it stops.
/// </summary>
internal static class ProcessSupervisor
{
    private const int ChunkSize = 16 * 1024;

    /// <summary>
    /// Hands <paramref name="text"/> to a shell and records what happens on
    /// <paramref name="command"/>. Returns once the process has started; the rest happens on its
    /// own.
    /// </summary>
    internal static void Spawn(
        Command command,
        string text,
        ShellSpec shell,
        string workingDirectory,
        TimeProvider time)
    {
        var start = new ProcessStartInfo
        {
            FileName = shell.File,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (shell.Raw)
        {
            // cmd.exe's own quoting rules, not the C runtime's. /s strips exactly the one outer
            // pair added here, which is what lets the text through as it was written.
            start.Arguments = string.Join(' ', shell.Prefix) + " \"" + text + "\"";
        }
        else
        {
            foreach (string argument in shell.Prefix)
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add(text);
        }

        Process process;

        try
        {
            process = new Process { StartInfo = start };
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException
            or System.IO.FileNotFoundException or PlatformNotSupportedException)
        {
            command.MarkFailed($"{shell.File}: {exception.Message}");
            return;
        }

        command.MarkStarted(process);

        // No standard input. There is no channel through which a caller could type into a
        // running command, so leaving the pipe open would leave anything that reads stdin
        // waiting for bytes that can never arrive; closing it gives them end-of-file at once,
        // which is what a command run without a terminal expects.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // It exited before the pipe could be closed, which closes it.
        }

        _ = Task.Run(() => FollowAsync(command, process, time));
    }

    private static async Task FollowAsync(Command command, Process process, TimeProvider time)
    {
        try
        {
            Task output = PumpAsync(process.StandardOutput.BaseStream, command.Stdout, time);
            Task error = PumpAsync(process.StandardError.BaseStream, command.Stderr, time);

            await process.WaitForExitAsync().ConfigureAwait(false);

            // After the wait, because a pipe can still hold what was written just before the
            // process stopped; the reads end when the write ends of the pipes are closed, which
            // exiting does.
            await Task.WhenAll(output, error).ConfigureAwait(false);

            command.MarkExited(process.ExitCode);
        }
        catch (Exception exception)
        {
            Diagnostics.Report($"following {command.Id}", exception);
            command.MarkFailed(exception.Message);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task PumpAsync(Stream from, OutputFile to, TimeProvider time)
    {
        byte[] buffer = new byte[ChunkSize];

        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer).ConfigureAwait(false);

                if (read == 0)
                {
                    return;
                }

                await to.AppendAsync(buffer.AsMemory(0, read), time.GetUtcNow(), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The command was killed, or its directory removed underneath it. Either way there
            // is no longer anywhere for these bytes to go.
            Diagnostics.Report($"reading {Path.GetFileName(to.Path)}", exception);
        }
    }
}
