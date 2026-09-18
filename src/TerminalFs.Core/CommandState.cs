namespace TerminalFs.Core;

/// <summary>Where a command has got to.</summary>
public enum CommandState
{
    /// <summary>The id is taken and the text is still being written. Nothing has started.</summary>
    Reserved,

    /// <summary>A process is running.</summary>
    Running,

    /// <summary>It finished, and said so with a zero exit code.</summary>
    Completed,

    /// <summary>
    /// It did not. A non-zero exit, a kill, or a shell that could not be started all end here: to
    /// a caller they are the same news, and the reason is on <c>stderr</c>.
    /// </summary>
    Error,

    /// <summary>
    /// It was refused before it ran, so there is no process, no exit code and no output. Its
    /// directory holds <c>command</c>, <c>status</c> and <c>reason</c>, and nothing else: every
    /// other file would be describing a process that does not exist.
    /// </summary>
    Denied,
}
