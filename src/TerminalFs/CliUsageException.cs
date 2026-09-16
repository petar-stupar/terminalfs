namespace TerminalFs;

/// <summary>A command line this program cannot act on, with the reason for a caller.</summary>
internal sealed class CliUsageException : Exception
{
    internal CliUsageException(string message) : base(message)
    {
    }

    internal CliUsageException(string message, Exception innerException) : base(message, innerException)
    {
    }

    internal CliUsageException()
    {
    }
}
