using System.Globalization;
using TerminalFs.Internal.Sessions;

namespace TerminalFs;

/// <summary>What <c>terminalfs session</c> can be asked to do.</summary>
internal enum SessionAction
{
    /// <summary>Start a session's server in the background and mount it.</summary>
    Start,

    /// <summary>Stop a session and remove everything it left.</summary>
    Stop,

    /// <summary>Stop the sessions nobody stopped.</summary>
    Collect,

    /// <summary>Run a session's server in the foreground.</summary>
    Serve,
}

/// <summary>The <c>terminalfs session</c> command line, parsed.</summary>
internal sealed record SessionOptions
{
    /// <summary>What to do.</summary>
    internal SessionAction Action { get; init; }

    /// <summary>The session, for everything but <c>gc</c>.</summary>
    internal string? Id { get; init; }

    /// <summary>The directory the session's commands run in, or null for this process's.</summary>
    internal string? WorkingDirectory { get; init; }

    /// <summary>
    /// How old a session <c>gc</c> stops even with its server still running, or null to stop only
    /// sessions whose server is gone.
    /// </summary>
    internal TimeSpan? OlderThan { get; init; }

    /// <summary>Print usage and stop.</summary>
    internal bool Help { get; init; }

    /// <summary>
    /// Parses the words after <c>session</c>, or throws on anything it does not know or anything
    /// missing.
    /// </summary>
    internal static SessionOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new CliUsageException("session: say start, stop or gc");
        }

        if (args[0] is "--help" or "-h")
        {
            return new SessionOptions { Help = true };
        }

        var options = new SessionOptions
        {
            Action = args[0] switch
            {
                "start" => SessionAction.Start,
                "stop" => SessionAction.Stop,
                "gc" => SessionAction.Collect,
                "serve" => SessionAction.Serve,
                _ => throw new CliUsageException($"session: unknown action '{args[0]}'"),
            },
        };

        for (int at = 1; at < args.Length; at++)
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
                string? value = inline ?? (at + 1 < args.Length ? args[++at] : null);

                return string.IsNullOrEmpty(value) ? throw new CliUsageException($"{name} needs a value") : value;
            }

            options = name switch
            {
                "--help" or "-h" => options with { Help = true },
                "--id" => options with { Id = Value() },
                "--cwd" => options with { WorkingDirectory = Value() },
                "--older-than" => options with { OlderThan = Duration(name, Value()) },
                _ => throw new CliUsageException($"unknown option '{argument}'"),
            };
        }

        return options.Help ? options : options.Validated();
    }

    private SessionOptions Validated()
    {
        string action = Action == SessionAction.Collect ? "gc" : Action.ToString().ToLowerInvariant();

        // A flag that does nothing for this action is refused rather than ignored: a hook that
        // passes one believes it did something.
        if (OlderThan is not null && Action != SessionAction.Collect)
        {
            throw new CliUsageException($"session {action}: --older-than is for gc");
        }

        if (WorkingDirectory is not null && Action is not (SessionAction.Start or SessionAction.Serve))
        {
            throw new CliUsageException($"session {action}: --cwd is for start and serve");
        }

        if (Action == SessionAction.Collect)
        {
            return Id is null
                ? this
                : throw new CliUsageException("session gc: collects every session, so takes no --id");
        }

        if (Id is null)
        {
            throw new CliUsageException($"session {action}: --id is required");
        }

        if (!SessionPaths.IsValidId(Id))
        {
            throw new CliUsageException(
                $"--id: '{Id}' is not a session id. It becomes a directory name, so it is up to "
                + $"{SessionPaths.MaxIdLength} letters, digits, '_', '-' and '.', not starting with '.'");
        }

        return this;
    }

    /// <summary>A number of seconds, or a number with <c>s</c>, <c>m</c>, <c>h</c> or <c>d</c>.</summary>
    private static TimeSpan Duration(string name, string text)
    {
        (string digits, double unit) = text[^1] switch
        {
            's' => (text[..^1], 1),
            'm' => (text[..^1], 60),
            'h' => (text[..^1], 3600),
            'd' => (text[..^1], 86400),
            _ => (text, 1),
        };

        // Ten years is not a limit anybody means; it is where TimeSpan would overflow instead.
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            && count * unit <= 3650 * 86400
            ? TimeSpan.FromSeconds(count * unit)
            : throw new CliUsageException($"{name}: '{text}' is not a duration, such as 90s, 30m, 12h or 7d");
    }

    /// <summary>How to use the command.</summary>
    internal const string Usage = """
        usage: terminalfs session start --id <id> [--cwd <dir>]
               terminalfs session stop --id <id>
               terminalfs session gc [--older-than <duration>]
               terminalfs session serve --id <id> [--cwd <dir>]

          start       start a server for this session on a free loopback port, mount it
                      at <runtime-dir>/terminalfs/<id>, and print that path. Starting a
                      session that is already mounted prints its path again, and keeps
                      the directory it was started in
          stop        stop the session's server, which kills its commands, unmount it and
                      remove its directory. Safe to run when there is nothing to stop
          gc          stop every session whose server is gone, and with --older-than every
                      session older than that, and remove what sessions left behind
          serve       what start runs in the background: the session's server, in the
                      foreground, until it is stopped

          --id <id>                 the session: letters, digits, '_', '-' and '.'
          --cwd <dir>               the directory the session's commands run in; this
                                    one by default
          --older-than <duration>   also stop sessions this old, and their commands, even
                                    with their server running: 90s, 30m, 12h, 7d
          --help, -h                print this and stop

        Linux only for now. Mounting needs root, so start runs mount and umount through
        sudo unless it is root already, and it needs the same settings file the shared
        server does. The runtime directory is $XDG_RUNTIME_DIR, or $XDG_CACHE_HOME (~/.cache by
        default) when that is not set.
        """;
}
