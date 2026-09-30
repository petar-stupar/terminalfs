using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using NineP.Protocol;
using NineP.Protocol.Transports;
using NineP.Server;
using TerminalFs.Core;
using TerminalFs.Core.Permissions;
using TerminalFs.Internal.Hooks;
using TerminalFs.Internal.Mount;
using TerminalFs.Internal.Server;
using TerminalFs.Internal.Sessions;

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
            await Console.Error.WriteLineAsync(
                    IsSession(args) ? SessionOptions.Usage : args is ["hook", ..] ? HookUsage : CliOptions.Usage)
                .ConfigureAwait(false);

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

        if (IsSession(args))
        {
            return await SessionAsync(SessionOptions.Parse(args[1..])).ConfigureAwait(false);
        }

        if (args is ["hook", ..])
        {
            return await HookAsync(args[1..]).ConfigureAwait(false);
        }

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

    private static bool IsSession(string[] args) => args is ["session", ..];

    private static async Task<int> SessionAsync(SessionOptions options)
    {
        if (options.Help)
        {
            Console.WriteLine(SessionOptions.Usage);

            return 0;
        }

        SessionPaths paths = SessionPaths.Default;

        // Only asked for by what starts a server. A hook that stops its session after its
        // worktree was deleted runs in a directory that no longer exists, and asking would fail
        // the stop and leave the server running.
        string workingDirectory = options.Action is SessionAction.Start or SessionAction.Serve
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory))
            : string.Empty;

        // And then left, whatever the action. .NET resolves a program to run against the current
        // directory, so from one that was deleted kill, mount, umount and sudo could not be
        // started at all; and a caller sitting in a session's tree would keep it busy. Commands
        // run in workingDirectory, which is passed on explicitly. The root of the filesystem
        // this binary is on, not the temporary directory: $TMPDIR can be relative, or gone
        // along with whatever the caller was cleaning up.
        Environment.CurrentDirectory = Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";

        // Starting is refused where it cannot work; stopping and collecting are not, because
        // there is never harm in finding nothing to clear up.
        if (options.Action is SessionAction.Start or SessionAction.Serve && !OperatingSystem.IsLinux())
        {
            throw new MountException(
                "sessions are Linux-only for now. Elsewhere the tree is mounted through a container "
                + "that serves one tree at a time; run 'terminalfs --mount' for a single shared one.");
        }

        var sessions = new Sessions(paths, new SessionHost(), Console.Error.WriteLine);
        string id = options.Id ?? string.Empty;

        switch (options.Action)
        {
            case SessionAction.Start:
                // The path, alone, on standard output: it is what a hook hands its agent.
                Console.WriteLine(await StartSessionAsync(sessions, id, workingDirectory).ConfigureAwait(false));

                return 0;

            case SessionAction.Stop:
                await sessions.StopAsync(id, CancellationToken.None).ConfigureAwait(false);

                return 0;

            case SessionAction.Collect:
                Collected collected = await sessions.CollectAsync(options.OlderThan, CancellationToken.None)
                    .ConfigureAwait(false);
                Console.WriteLine(
                    (collected.Stopped == 1 ? "stopped 1 session" : $"stopped {collected.Stopped} sessions")
                    + (collected.Cleared == 0 ? string.Empty
                        : collected.Cleared == 1 ? ", cleared up 1 leftover"
                        : $", cleared up {collected.Cleared} leftovers"));

                if (collected.Failed > 0)
                {
                    await Console.Error.WriteLineAsync($"terminalfs: {collected.Failed} could not be cleared up")
                        .ConfigureAwait(false);
                }

                return collected.Failed > 0 ? 1 : 0;

            default:
                string mountPath = paths.MountPath(id);

                // Start makes the same checks before launching this, but serve can be run by
                // hand, and it is the one that has root mount over the directory.
                SessionFiles.SecureRoot(paths.Root);
                await Sessions.CheckMountPointAsync(new SessionHost(), mountPath, CancellationToken.None)
                    .ConfigureAwait(false);

                try
                {
                    return await ServeAsync(
                        new CliOptions
                        {
                            Listen = "tcp://127.0.0.1:0",
                            Mount = true,
                            MountPath = mountPath,
                            WorkingDirectory = workingDirectory,
                        }.Validated(),
                        mounted: port => SessionRecord.ForThisProcess(id, port, mountPath, workingDirectory)
                            .Write(paths.RecordPath(id))).ConfigureAwait(false);
                }
                catch (CliUsageException refusal)
                {
                    // Nothing here was typed by anybody, so the usage text would only bury the
                    // reason in the log start quotes from.
                    throw new MountException(refusal.Message);
                }
        }
    }

    /// <summary>Starts session <paramref name="id"/>, or finds it started, and returns where its tree is.</summary>
    private static async Task<string> StartSessionAsync(Sessions sessions, string id, string workingDirectory)
    {
        // Both checked here as well as by the server, because here is where they can be said: a
        // hook sees what start prints, and nothing of what the server logs.
        if (!Directory.Exists(workingDirectory))
        {
            throw new CliUsageException($"--cwd: {workingDirectory} is not a directory");
        }

        if (!File.Exists(Settings.DefaultPath))
        {
            throw new MountException(
                $"{Settings.DefaultPath} does not exist, and a session's server will not start "
                + "without the rules that say what it may not run. Write one with sane "
                + "defaults by running 'terminalfs --init-settings'.");
        }

        return await sessions.StartAsync(id, workingDirectory, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>terminalfs hook claude &lt;event&gt;</c>: what Claude Code runs, with the event's JSON on
    /// standard input.
    /// </summary>
    /// <remarks>
    /// Claude Code reads a hook's exit code as well as what it prints. 2 blocks a tool call
    /// whatever else happened, and anything else but 0 is a failure it reports and then carries on
    /// past — so a check that falls over lets the call through. Every failure here is turned into
    /// an answer instead: a refusal when the call was headed for a session tree, and nothing when
    /// it was not.
    /// </remarks>
    private static async Task<int> HookAsync(string[] args)
    {
        if (args is ["--help" or "-h"] or ["claude", "--help" or "-h"])
        {
            Console.WriteLine(HookUsage);

            return 0;
        }

        if (args is not ["claude", "session-start" or "session-end" or "pre-tool-use"])
        {
            throw new CliUsageException("hook: say 'claude session-start', 'claude session-end' or 'claude pre-tool-use'");
        }

        string json = await Console.In.ReadToEndAsync().ConfigureAwait(false);
        SessionPaths paths = SessionPaths.Default;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (args[1] == "pre-tool-use")
        {
            try
            {
                ClaudeDecision? decision = new ClaudeHook(paths, Environment.GetEnvironmentVariable, home, ClaudeSettings.ManagedDirectory)
                    .PreToolUse(ClaudeHookInput.Parse(json));

                if (decision is not null)
                {
                    Console.WriteLine(decision.Json());
                }
            }
#pragma warning disable CA1031 // Whatever it was, the call gets an answer rather than an exit code.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                // Nothing that could not be read is let through on the strength of a crash, and
                // nothing that never went near a tree is refused because of one.
                if (json.Contains(paths.Root, StringComparison.Ordinal) || json.Contains("/terminalfs", StringComparison.Ordinal))
                {
                    Console.WriteLine(new ClaudeDecision("deny", $"terminalfs could not check this call: {exception.Message}").Json());
                }
                else
                {
                    await Console.Error.WriteLineAsync("terminalfs: " + exception.Message).ConfigureAwait(false);
                }
            }

            return 0;
        }

        ClaudeHookInput input;

        try
        {
            input = ClaudeHookInput.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new MountException($"the {args[1]} hook's input is not what Claude Code sends: {exception.Message}");
        }

        if (!SessionPaths.IsValidId(input.SessionId))
        {
            throw new MountException($"'{input.SessionId}' cannot name a session tree");
        }

        // As a session command does, and for the same reasons: nothing here should keep a tree
        // busy, or fail to start a program because the session's directory went away.
        Environment.CurrentDirectory = Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";

        if (args[1] == "session-end")
        {
            // Sessions only ever start on Linux, so elsewhere there is nothing to stop.
            if (OperatingSystem.IsLinux())
            {
                SessionHost.StopDetached(input.SessionId);
            }

            return 0;
        }

        var sessions = new Sessions(paths, new SessionHost(), Console.Error.WriteLine);

        string context;

        try
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new MountException("sessions are Linux-only for now");
            }

            string mountPath = await StartSessionAsync(sessions, input.SessionId, input.WorkingDirectory).ConfigureAwait(false);

            context = $"This session's terminalfs tree is mounted at {mountPath}. Wherever the terminalfs skill "
                + $"writes <mount>, the path is {mountPath}.";
        }
        catch (Exception exception) when (exception is MountException or CliUsageException or CommandException or IOException or UnauthorizedAccessException)
        {
            // The session goes on without a tree, and the agent is told so rather than left to
            // find out by writing to a directory that is not there.
            await Console.Error.WriteLineAsync("terminalfs: " + exception.Message).ConfigureAwait(false);

            context = $"terminalfs could not start a tree for this session, so the terminalfs skill cannot be used: {exception.Message}";
        }

        Console.WriteLine(ClaudeHook.SessionContext(context));

        return 0;
    }

    /// <summary>How to use <c>terminalfs hook</c>.</summary>
    internal const string HookUsage = """
        usage: terminalfs hook claude session-start
               terminalfs hook claude session-end
               terminalfs hook claude pre-tool-use

        What the Claude Code plugin runs, with the hook's JSON on standard input.

          session-start   start this session's tree, and tell the agent where it is
          session-end     stop it
          pre-tool-use    check a command written to the tree against the session's
                          Claude Code permission rules, and refuse any other write into it
        """;

    /// <param name="options">What to serve and where.</param>
    /// <param name="mounted">Told the port once the tree is mounted, if it is.</param>
    private static async Task<int> ServeAsync(CliOptions options, Action<int>? mounted = null)
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

        // Port 0 asks the system for a free port, and the mount has to name the one it gave.
        if (BoundPort(server.Endpoints) is int bound)
        {
            mount = mount with { NinePPort = bound };
        }

        // The signals are hooked before anything is mounted, not after. Mounting can run for
        // minutes — an image build, thirty polls waiting for Samba, a sudo prompt — and a Ctrl-C
        // in that window would otherwise take the default action and kill the process outright,
        // leaving the container running, a half-made mount attached, and every command this
        // server started still going with nothing watching them.
        using var shutdown = new Shutdown();

        bool announced = true;

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

            // A tree nobody was told about is one nobody will stop, so failing to say it is
            // mounted is failing to start: it is taken down again on the way out below.
            try
            {
                if (!shutdown.Token.IsCancellationRequested)
                {
                    mounted?.Invoke(mount.NinePPort);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Console.Error.WriteLineAsync($"terminalfs: mounted, but could not say so: {exception.Message}")
                    .ConfigureAwait(false);
                announced = false;
            }
        }

        bool faulted = !announced
            || (!shutdown.Token.IsCancellationRequested
                && await Task.WhenAny(serving, shutdown.Requested).ConfigureAwait(false) == serving);

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

        if (faulted && announced)
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

    /// <summary>The TCP port the server actually bound, or null when it is on none.</summary>
    internal static int? BoundPort(IEnumerable<NinePAddress> endpoints) => endpoints
        .Select(endpoint => Uri.TryCreate(endpoint.ToString(), UriKind.Absolute, out Uri? address)
            && address.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            && address.Port > 0
                ? address.Port
                : (int?)null)
        .FirstOrDefault(port => port is not null);

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
