namespace TerminalFs.Core;

/// <summary>One of a command's output streams, as a file of the tree.</summary>
public sealed class TerminalOutput : TerminalNode
{
    private readonly Command command;

    internal TerminalOutput(string name, string key, Command command, OutputFile file)
        : base(name, TerminalNodeKind.Output, key)
    {
        this.command = command;
        File = file;
    }

    /// <summary>The bytes, and how many of them there are.</summary>
    public OutputFile File { get; }

    /// <inheritdoc />
    public override uint Revision => File.Revision;

    /// <inheritdoc />
    public override DateTimeOffset? ModifiedAt => File.LastWrite;

    /// <inheritdoc />
    public override Command.Lease? Lease() => command.Open();
}
