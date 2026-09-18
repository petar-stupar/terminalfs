namespace TerminalFs.Core;

/// <summary>
/// Where this library reports a failure it recovered from.
/// </summary>
/// <remarks>
/// These are the failures with nowhere else to go. A command refused at <c>/ctl</c> travels back
/// to the caller as a <see cref="CommandException"/> and says so again in its own <c>status</c>
/// and <c>reason</c>, and a command that fails while running says so in its <c>status</c> and
/// <c>stderr</c>. A settings file that stopped parsing halfway
/// through a session has neither channel: the rules that are still in force are the ones loaded
/// before the edit, and nobody asked a question this could be the answer to.
/// </remarks>
public static class Diagnostics
{
    private static Action<string>? sink;

    /// <summary>Sends every report to <paramref name="to"/>. Null silences them again.</summary>
    public static void To(Action<string>? to) => sink = to;

    /// <summary>Reports a recovered failure, naming what was being done when it happened.</summary>
    internal static void Report(string doing, Exception exception) =>
        sink?.Invoke($"terminalfs: {doing}: {exception.GetType().Name}: {exception.Message}");

    /// <summary>
    /// Reports something noticed rather than caught. Public because the server above this
    /// library has the same kind of failure to report — a clunk cannot carry an error back to
    /// whoever wrote the bytes — and should report it to the same place.
    /// </summary>
    public static void Report(string message) => sink?.Invoke("terminalfs: " + message);
}
