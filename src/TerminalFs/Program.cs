using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;
using TerminalFs.Core;
using TerminalFs.Core.Permissions;
using TerminalFs.Internal.Mount;
using TerminalFs.Internal.Server;

namespace TerminalFs;

/// <summary>The <c>terminalfs</c> command.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (CliUsageException exception)
        {
            await Console.Error.WriteLineAsync("terminalfs: " + exception.Message).ConfigureAwait(false);
            await Console.Error.WriteLineAsync(CliOptions.Usage).ConfigureAwait(false);

            return 2;
        }
        catch (Exception exception) when (exception is MountException or CommandException)
        {
            await Console.Error.WriteLineAsync("terminalfs: " + exception.Message).ConfigureAwait(false);

            return 1;
        }
        // SocketException is not an IOException, so the catch below still gets it.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync("terminalfs: " + exception.Message).ConfigureAwait(false);

            return 1;
        }
        catch (SocketException exception)
        {
            // A port already taken is the ordinary way this fails, and a stack trace is the wrong
            // way to say so.
            await Console.Error.WriteLineAsync("terminalfs: cannot listen: " + exception.Message)
                .ConfigureAwait(false);

            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        // Everything recovered from goes here: a settings file that stopped parsing, a command
        // whose output could not be written, a clunk that could not carry its own error back.
        Diagnostics.To(Console.Error.WriteLine);

        CliOptions options = CliOptions.Parse(args);

        if (options.Help)
        {
            Console.WriteLine(CliOptions.Usage);

            return 0;
        }

        // Before Validated(), as --help is: asking what this binary is cannot be refused for a
        // combination of flags that has nothing to do with the answer.
        if (options.Version)
        {
            Console.WriteLine(Versions.Report);

            return 0;
        }

        options = options.Validated();

        if (options.InitSettings)
        {
            Settings.WriteDefaults(options.SettingsPath);

            Console.WriteLine($"wrote {options.SettingsPath} with {Rules(Settings.Defaults.Count)}");

            return 0;
        }

        if (options.Unmount)
        {
            await Mounter.UnmountAsync(options.MountSettings).ConfigureAwait(false);

            return 0;
        }

        if (options.RestartContainer)
        {
            Console.WriteLine("taking the existing bridge down");
            await Mounter.UnmountAsync(options.MountSettings).ConfigureAwait(false);
        }

        return await ServeAsync(options).ConfigureAwait(false);
    }

    private static async Task<int> ServeAsync(CliOptions options)
    {
        bool mounting = options.Mount || options.RestartContainer;
        MountSettings mount = options.MountSettings;

        // Loopback by default, including for the container bridge: Docker Desktop forwards
        // host.docker.internal to the host's loopback, so the bridge reaches a server bound here
        // and nothing off this machine does.
        string listen = options.ListenAddress;

        // Before the socket, and fatal if it fails. A server that ran commands under rules
        // nobody wrote would be worse than one that did not start, and this is the last moment
        // at which refusing costs nothing.
        using SettingsWatcher settings = LoadSettings(options);

        Console.WriteLine($"rules {settings.Path} ({Rules(settings.Current.Count)})");

        using var registry = CommandRegistry.Create(new CommandOptions
        {
            Shell = options.Shell,
            WorkingDirectory = options.WorkingDirectory,
            KeepAfterExit = options.Keep,
            Settle = options.Settle,
            WaitTimeout = options.WaitTimeout,
            Settings = settings,

            // Only when somebody has said where the tree will be: this process is mounting it, or
            // --path named the place they will mount it themselves. Passing the default otherwise
            // would put a directory that is not there into the skill an agent follows, which is
            // worse than the placeholder it replaces.
            MountPath = mounting || options.MountPath is not null ? mount.MountPath : null,
        });

        var trace = options.LogRequests ? new RequestTrace() : null;

        await using var server = new NinePServer(new ServerOptions
        {
            Listen = [NinePAddress.Parse(listen)],
            RequestLog = trace,
            Logger = new StandardErrorLogger(),
        });

        Task serving = server.ServeAsync(new TerminalTree(registry));
        await server.ListeningAsync().ConfigureAwait(false);

        foreach (NinePAddress address in server.Endpoints)
        {
            Console.WriteLine(
                $"listening {address} shell={registry.Shell.File} cwd={registry.WorkingDirectory} "
                    + $"keep={options.KeepSeconds.ToString(CultureInfo.InvariantCulture)}s");
        }

        // The signals are hooked before anything is mounted, not after. Mounting can run for
        // minutes — an image build, thirty polls waiting for Samba, a sudo prompt — and a Ctrl-C
        // in that window would otherwise take the default action and kill the process outright,
        // leaving the container running, a half-made mount attached, and every command this
        // server started still going with nothing watching them.
        using var shutdown = new Shutdown();

        if (mounting)
        {
            try
            {
                await Mounter.MountAsync(mount, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await Console.Error.WriteLineAsync("terminalfs: interrupted while mounting")
                    .ConfigureAwait(false);
            }
        }

        bool faulted = !shutdown.Token.IsCancellationRequested
            && await Task.WhenAny(serving, shutdown.Requested).ConfigureAwait(false) == serving;

        if (mounting)
        {
            await Mounter.UnmountAsync(mount, CancellationToken.None).ConfigureAwait(false);
        }

        if (trace is not null)
        {
            await Console.Error.WriteLineAsync(trace.Report()).ConfigureAwait(false);
        }

        await server.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Awaiting the serve task rethrows whatever stopped it, which is the only place the
        // reason exists.
        await serving.ConfigureAwait(false);

        if (faulted)
        {
            await Console.Error.WriteLineAsync("terminalfs: the server stopped on its own")
                .ConfigureAwait(false);
        }

        // Disposing the registry kills every command still running. They were started through
        // this server and their output goes nowhere once it is gone, so leaving them would leave
        // work running that nobody can see, stop, or collect.
        return faulted ? 1 : 0;
    }

    /// <summary>
    /// Reads the settings file, turning the ordinary first-run failure into something that says
    /// what to do about it.
    /// </summary>
    private static SettingsWatcher LoadSettings(CliOptions options)
    {
        try
        {
            return SettingsWatcher.Start(options.SettingsPath, TimeSpan.FromSeconds(2));
        }
        catch (CommandException refusal)
        {
            throw new CliUsageException(
                $"{options.SettingsPath}: {refusal.Message}. This server runs whatever is written "
                    + "to /ctl, so it will not start without the rules that say what it may not "
                    + "run. Write a file with sane defaults by running 'terminalfs "
                    + "--init-settings'.",
                refusal);
        }
    }

    private static string Rules(int count) => count == 1 ? "1 deny rule" : $"{count} deny rules";

    /// <summary>
    /// The signals that mean stop, hooked for the life of the run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SIGTERM</c> is the one that matters. Hooking only <c>Console.CancelKeyPress</c> catches
    /// Ctrl-C and nothing else, so an ordinary <c>kill</c>, a logout, or a supervisor stopping
    /// this process would leave the mount attached, the bridge container running, and every
    /// command still going.
    /// </para>
    /// <para>
    /// <c>SIGKILL</c> cannot be caught by anything, so an orphaned mount is still possible.
    /// <c>--unmount</c> exists to clear one up and is safe to run when nothing is mounted, and
    /// the output of a server that was killed is swept by the next one to start.
    /// </para>
    /// </remarks>
    private sealed class Shutdown : IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly List<PosixSignalRegistration> signals;

        internal Shutdown()
        {
            signals =
            [
                PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop),
                PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop),
            ];

            if (!OperatingSystem.IsWindows())
            {
                signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop));
            }
        }

        /// <summary>Cancelled when a stop signal arrives.</summary>
        internal CancellationToken Token => stopping.Token;

        /// <summary>Completes when a stop signal arrives.</summary>
        internal Task Requested => Task.Delay(Timeout.InfiniteTimeSpan, stopping.Token)
            .ContinueWith(static _ => { }, TaskScheduler.Default);

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (PosixSignalRegistration signal in signals)
            {
                signal.Dispose();
            }

            stopping.Dispose();
        }

        private void Stop(PosixSignalContext context)
        {
            // Take responsibility for the signal: without this the runtime terminates the process
            // where it stands and the cleanup never runs.
            context.Cancel = true;
            stopping.Cancel();
        }
    }
}
