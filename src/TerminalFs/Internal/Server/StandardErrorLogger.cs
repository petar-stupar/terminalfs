using Microsoft.Extensions.Logging;

namespace TerminalFs.Internal.Server;

/// <summary>
/// The 9P library's log, on standard error.
/// </summary>
/// <remarks>
/// Left at the null logger, the library's own "handler threw" line goes nowhere, and an exception
/// this code did not expect becomes an <c>EIO</c> the client cannot explain and the server never
/// mentions. That is the worst shape a failure can take here: a tree that looks like it is
/// working. Warnings and above are printed always, not only under <c>--log-requests</c>, because
/// nobody turns on a flag for a problem they have not been told they have.
/// </remarks>
internal sealed class StandardErrorLogger : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        if (!IsEnabled(logLevel))
        {
            return;
        }

        string reason = exception is null ? string.Empty : $": {exception.GetType().Name}: {exception.Message}";

        Console.Error.WriteLine($"terminalfs: [9p] {formatter(state, exception)}{reason}");
    }
}
