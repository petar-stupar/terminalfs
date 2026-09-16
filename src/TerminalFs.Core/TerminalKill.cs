namespace TerminalFs.Core;

/// <summary>
/// <c>/cmd/&lt;id&gt;/kill</c>: writing anything here ends the command.
/// </summary>
/// <remarks>
/// In the command's own directory rather than as one shared file, for the same reason the control
/// files are: concurrent writes to a single path are merged by the client and arrive as one, so a
/// shared kill file would silently drop all but one of several kills issued at once.
/// </remarks>
public sealed class TerminalKill : TerminalNode
{
    private readonly Command command;

    internal TerminalKill(string key, Command command)
        : base("kill", TerminalNodeKind.Control, key) => this.command = command;

    /// <inheritdoc />
    public override uint Revision => command.StatusRevision;

    /// <inheritdoc />
    public override DateTimeOffset? ModifiedAt => command.ChangedAt;

    /// <inheritdoc />
    public override Command.Lease? Lease() => command.Open();

    /// <summary>Ends the command, and everything it started.</summary>
    /// <remarks>
    /// Writing to a command that has already stopped is not an error. A caller killing something
    /// races the command finishing on its own, and both outcomes are the one they asked for.
    /// </remarks>
    public void Kill() => command.Kill();
}
