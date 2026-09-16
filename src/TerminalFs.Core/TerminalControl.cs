namespace TerminalFs.Core;

/// <summary>
/// A file a command is written to. Its name is the command's id.
/// </summary>
/// <remarks>
/// Every command gets a file of its own rather than sharing one <c>/ctl</c>, because a client
/// merges concurrent writes to one path in its page cache and sends a single write: four callers
/// writing at once reached the server as one, and three commands were lost with no error. See
/// <see cref="ControlSession"/>.
/// </remarks>
public abstract class TerminalControl : TerminalNode
{
    private protected TerminalControl(string name, string key)
        : base(name, TerminalNodeKind.Control, key)
    {
    }

    /// <summary>Opens the file, taking the id for a command that has not been written yet.</summary>
    /// <exception cref="CommandException">The id is not usable, or is already taken.</exception>
    public abstract ControlSession Open();
}
