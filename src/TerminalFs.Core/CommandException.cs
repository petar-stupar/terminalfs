namespace TerminalFs.Core;

/// <summary>
/// Something this library refused, carrying both the reason a reader should see and the POSIX
/// error number it amounts to.
/// </summary>
/// <remarks>
/// Both are needed because the dialects carry different things. 9P2000 and 9P2000.u carry an
/// error string, so a caller sees the sentence; 9P2000.L carries a number and nothing else, so on
/// a Linux mount the number is the entire message. A refusal that reported only one of them would
/// be mute on half the clients that can reach this tree — and a refusal here is usually the only
/// thing standing between a caller and a command that should not run.
/// </remarks>
public sealed class CommandException : Exception
{
    /// <summary>Creates an exception carrying <paramref name="message"/> and its error number.</summary>
    public CommandException(string message, int errno) : base(message) => Errno = errno;

    /// <summary>Creates an exception whose refusal is an invalid argument.</summary>
    public CommandException(string message) : this(message, CommandErrno.InvalidArgument)
    {
    }

    /// <summary>Creates an exception carrying <paramref name="message"/> over an inner cause.</summary>
    public CommandException(string message, Exception innerException) : base(message, innerException) =>
        Errno = CommandErrno.InvalidArgument;

    /// <summary>Creates an exception with no message of its own.</summary>
    public CommandException() => Errno = CommandErrno.InvalidArgument;

    /// <summary>The POSIX error number this refusal amounts to.</summary>
    public int Errno { get; }
}
