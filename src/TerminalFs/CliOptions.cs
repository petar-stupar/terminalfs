using System.Globalization;
using System.Net;
using TerminalFs.Core.Permissions;
using TerminalFs.Internal.Mount;

namespace TerminalFs;

/// <summary>The command line, parsed.</summary>
internal sealed record CliOptions
{
    /// <summary>The 9P address to listen on.</summary>
    internal string Listen { get; init; } = "";

    /// <summary>
    /// Where the tree should appear on this machine, or null if nobody said.
    /// </summary>
    /// <remarks>
    /// Null rather than the default, because the two mean different things to the served skill:
    /// it prints a path somebody stated and keeps a placeholder otherwise. Mounting resolves the
    /// default itself, so <see cref="MountSettings"/> is unaffected.
    /// </remarks>
    internal string? MountPath { get; init; }

    /// <summary>Whether to mount after starting the server.</summary>
    internal bool Mount { get; init; }

    /// <summary>Whether the container bridge was asked for explicitly.</summary>
    internal bool Docker { get; init; }

    /// <summary>Unmount and stop the bridge, without starting a server.</summary>
    internal bool Unmount { get; init; }

    /// <summary>Recreate the bridge container and mount again.</summary>
    internal bool RestartContainer { get; init; }

    /// <summary>The shell commands are handed to, or null for the caller's own.</summary>
    internal string? Shell { get; init; }

    /// <summary>The directory commands run in, or null for this process's.</summary>
    internal string? WorkingDirectory { get; init; }

    /// <summary>Where the deny rules are read from.</summary>
    internal string SettingsPath { get; init; } = Settings.DefaultPath;

    /// <summary>Write a settings file with sane defaults and stop.</summary>
    internal bool InitSettings { get; init; }

    /// <summary>How long a finished command is kept after the last read of it.</summary>
    internal int KeepSeconds { get; init; } = 60;

    /// <summary>How long a read of <c>wait</c> blocks before answering <c>running</c>.</summary>
    internal int WaitSeconds { get; init; } = 25;

    /// <summary>The port the 9P server listens on.</summary>
    internal int NinePPort { get; init; } = 15641;

    /// <summary>The host port the bridge publishes its share on.</summary>
    internal int SmbPort { get; init; } = 14451;

    /// <summary>Report each kind of request the first time it arrives, and each refusal.</summary>
    internal bool LogRequests { get; init; }

    /// <summary>Print usage and stop.</summary>
    internal bool Help { get; init; }

    /// <summary>Print the version of this program and of the 9P library, and stop.</summary>
    internal bool Version { get; init; }

    /// <summary>How this run should mount, given what was asked for and what the machine is.</summary>
    /// <remarks>
    /// The path is made absolute here and nowhere else. The mount table records absolute paths, so
    /// a relative <c>--path</c> would mount correctly and then fail its own ownership check on the
    /// way back out: <c>--unmount --path ./x</c> would refuse to detach the mount it had just made.
    /// </remarks>
    internal MountSettings MountSettings => new(
        Path.GetFullPath(MountPath ?? MountSettings.DefaultMountPath),
        MountSettings.StrategyFor(Docker),
        ListenPort ?? NinePPort,
        SmbPort);

    /// <summary>The address the server binds: <c>--listen</c> if given, otherwise loopback.</summary>
    internal string ListenAddress => Listen.Length > 0
        ? Listen
        : $"tcp://127.0.0.1:{NinePPort.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>How long a finished command is kept.</summary>
    internal TimeSpan Keep => TimeSpan.FromSeconds(KeepSeconds);

    /// <summary>How long a read of <c>wait</c> blocks.</summary>
    internal TimeSpan WaitTimeout => TimeSpan.FromSeconds(WaitSeconds);

    /// <summary>
    /// Checks the combinations that would otherwise fail somewhere far from their cause, and
    /// returns the options unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Binding off loopback is refused outright, with no flag to override it. This server runs
    /// whatever is written to <c>/ctl</c>, as the user who started it, with their environment and
    /// their credentials; 9P offers authentication and nothing here uses it, so whoever can open
    /// the socket gets a shell. A read-only tree of documentation can reasonably be offered to a
    /// network. This cannot, and a flag to do it anyway would only be a way to get it wrong.
    /// </para>
    /// <para>
    /// <c>--listen</c> and <c>--port</c> describe the same thing and can disagree. A mount is told
    /// a port, so <c>--listen tcp://127.0.0.1:9999 --mount</c> would serve on 9999 and mount
    /// 15641: at best a mount that fails, at worst one that silently attaches to a different
    /// <c>terminalfs</c> still running on the default port. The listen address wins, because it is
    /// the more specific of the two.
    /// </para>
    /// </remarks>
    internal CliOptions Validated()
    {
        if (KeepSeconds is < 0 or > 86400)
        {
            throw new CliUsageException("--keep: a command is kept for between 0 and 86400 seconds");
        }

        if (WaitSeconds is < 1 or > 3600)
        {
            throw new CliUsageException("--wait-timeout: a read of wait blocks for 1 to 3600 seconds");
        }

        if (Listen.Length == 0)
        {
            return this;
        }

        if (!Uri.TryCreate(Listen, UriKind.Absolute, out Uri? address))
        {
            throw new CliUsageException($"--listen: '{Listen}' is not an address");
        }

        bool loopback = address.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(address.Host, out IPAddress? ip)
            && IPAddress.IsLoopback(ip);

        loopback = loopback || address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        // A unix socket has no host to be loopback or not: it is a path on this machine, reachable
        // only by something already on it, which is more local than loopback rather than less.
        bool unix = address.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase);

        if (!loopback && !unix)
        {
            throw new CliUsageException(
                $"--listen {Listen} would let anything that can reach that address run commands "
                + "as you, with your environment and your credentials. Bind loopback.");
        }

        if (Mount || RestartContainer)
        {
            if (!address.Scheme.Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliUsageException(
                    $"--listen {Listen} cannot be mounted from here: the mount is made with "
                    + "trans=tcp, so the server has to be listening on TCP.");
            }
        }

        return this;
    }

    /// <summary>The TCP port of <c>--listen</c>, or null when it names no TCP port.</summary>
    private int? ListenPort =>
        Listen.Length > 0
        && Uri.TryCreate(Listen, UriKind.Absolute, out Uri? address)
        && address.Port > 0
            ? address.Port
            : null;

    /// <summary>Parses <paramref name="args"/>, or throws on a spelling it does not know.</summary>
    internal static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (int at = 0; at < args.Length; at++)
        {
            string argument = args[at];
            string name = argument;
            string? inline = null;

            int equals = argument.IndexOf('=', StringComparison.Ordinal);

            if (argument.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                name = argument[..equals];
                inline = argument[(equals + 1)..];
            }

            string Value()
            {
                // "--path=" parses as an inline value that happens to be empty, and an empty path
                // throws out of Path.GetFullPath long after anything is left to say about it.
                if (inline is not null)
                {
                    return inline.Length > 0 ? inline : throw new CliUsageException($"{name} needs a value");
                }

                if (at + 1 >= args.Length)
                {
                    throw new CliUsageException($"{name} needs a value");
                }

                return args[++at].Length > 0
                    ? args[at]
                    : throw new CliUsageException($"{name} needs a value");
            }

            int Port()
            {
                string text = Value();

                return int.TryParse(text, CultureInfo.InvariantCulture, out int port) && port is > 0 and < 65536
                    ? port
                    : throw new CliUsageException($"{name}: '{text}' is not a port");
            }

            int Seconds()
            {
                string text = Value();

                return int.TryParse(text, CultureInfo.InvariantCulture, out int seconds) && seconds >= 0
                    ? seconds
                    : throw new CliUsageException($"{name}: '{text}' is not a number of seconds");
            }

            // --init-settings takes an optional path, so it looks ahead rather than demanding one.
            bool HasValue() => inline is not null
                || (at + 1 < args.Length && !args[at + 1].StartsWith('-'));

            options = name switch
            {
                "--help" or "-h" => options with { Help = true },
                "--version" => options with { Version = true },
                "--listen" => options with { Listen = Value() },
                "--mount" => options with { Mount = true },
                "--mount-docker" => options with { Mount = true, Docker = true },
                "--unmount" => options with { Unmount = true },
                "--restart-docker-container" => options with { RestartContainer = true, Docker = true },
                "--path" => options with { MountPath = Value() },
                "--shell" => options with { Shell = Value() },
                "--cwd" => options with { WorkingDirectory = Value() },
                "--settings" => options with { SettingsPath = Value() },
                "--init-settings" => HasValue()
                    ? options with { InitSettings = true, SettingsPath = Value() }
                    : options with { InitSettings = true },
                "--keep" => options with { KeepSeconds = Seconds() },
                "--wait-timeout" => options with { WaitSeconds = Seconds() },
                "--port" => options with { NinePPort = Port() },
                "--smb-port" => options with { SmbPort = Port() },
                "--log-requests" => options with { LogRequests = true },
                _ => throw new CliUsageException($"unknown option '{argument}'"),
            };
        }

        return options;
    }

    /// <summary>How to use the command.</summary>
    internal const string Usage = """
        usage: terminalfs [--listen <url>] [--port <n>] [--shell <path>] [--cwd <dir>]
                          [--mount | --mount-docker] [--path <dir>] [--smb-port <n>]
                          [--settings <file>] [--init-settings [file]] [--keep <n>]
                          [--wait-timeout <n>] [--unmount] [--restart-docker-container]

          (no flags)                  serve the tree over 9P and print the address
          --mount                     serve, then mount it; Linux mounts 9P directly and
                                      macOS goes through a container that re-exports SMB
          --mount-docker              serve, then mount through the container everywhere
          --path <dir>                where to mount; ~/mnt/terminalfs by default. Stating it
                                      is also what puts a real path into the served skill when
                                      you mount the tree yourself
          --unmount                   unmount and remove the bridge, without serving
          --restart-docker-container  recreate the bridge container and mount again
          --listen <url>              9P address; tcp://127.0.0.1:<port> by default. This
                                      server runs commands as you, so an address off
                                      loopback is refused
          --port <n>                  the 9P port, 15641 by default
          --smb-port <n>              the host port the bridge publishes, 14451 by default
          --shell <path>              the shell commands are handed to; $SHELL, or
                                      /bin/sh, by default
          --cwd <dir>                 the directory commands run in; this one by default
          --settings <file>           where the deny rules are read from. The file must
                                      exist and parse or the server does not start
          --init-settings [file]      write a settings file with sane defaults, and stop.
                                      It refuses to overwrite one that is already there
          --keep <n>                  seconds a finished command is kept after the last
                                      read of it, 60 by default; 0 removes it at once
          --wait-timeout <n>          seconds a read of wait blocks before answering
                                      'running', 25 by default
          --log-requests              report each kind of 9P request the first time it
                                      arrives, and every kind of refusal
          --version                   print this program's version and the version of the
                                      9P library it is built against, and stop
          --help, -h                  print this and stop

        environment, read only when the matching flag is absent:

          SHELL                       the shell commands are handed to
          XDG_CONFIG_HOME             where the settings file is looked for
        """;
}
