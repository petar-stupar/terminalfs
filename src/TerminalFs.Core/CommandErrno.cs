namespace TerminalFs.Core;

/// <summary>
/// The POSIX numbers this library refuses with. They are restated here rather than taken from
/// the 9P library so that this project keeps knowing nothing about 9P; the server maps them onto
/// the wire.
/// </summary>
public static class CommandErrno
{
    /// <summary>The command, the id or the settings file was not what it had to be.</summary>
    public const int InvalidArgument = 22;

    /// <summary>No command by that name.</summary>
    public const int NotFound = 2;

    /// <summary>An id that is already taken. An id is one command, and only one.</summary>
    public const int Exists = 17;

    /// <summary>A deny rule refused the command.</summary>
    public const int NotPermitted = 1;

    /// <summary>More bytes were written to the control file than one command may carry.</summary>
    public const int TooLarge = 27;

    /// <summary>A directory that still has children cannot be removed.</summary>
    public const int NotEmpty = 39;

    /// <summary>Nothing in this tree may be written except the control file.</summary>
    public const int ReadOnly = 30;

    /// <summary>A directory was named where a file was meant.</summary>
    public const int IsDirectory = 21;

    /// <summary>A file was named where a directory was meant.</summary>
    public const int NotDirectory = 20;
}
