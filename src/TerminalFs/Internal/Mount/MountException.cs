namespace TerminalFs.Internal.Mount;

/// <summary>A mount that could not be carried out, carrying the reason a user should see.</summary>
internal sealed class MountException : Exception
{
    /// <summary>Creates an exception carrying <paramref name="message"/>.</summary>
    internal MountException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception carrying <paramref name="message"/> over an inner cause.</summary>
    internal MountException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
