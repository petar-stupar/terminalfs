using NineP.Protocol;
using NineP.Protocol.Auth;
using NineP.Server;
using TerminalFs.Core;

namespace TerminalFs.Internal.Server;

/// <summary>
/// The registry as a 9P filesystem. It owns the mapping from a node to a handler, which is the
/// whole of the 9P layer: the registry knows nothing about the protocol, and this knows nothing
/// about processes.
/// </summary>
internal sealed class TerminalTree : IFilesystem
{
    private readonly CommandRegistry registry;
    private readonly QidPaths qids = new();

    internal TerminalTree(CommandRegistry registry)
    {
        this.registry = registry;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Started = new TimeSpec(now.ToUnixTimeSeconds(), 0);
    }

    /// <summary>The time a node that never changes reports.</summary>
    internal TimeSpec Started { get; }

    /// <inheritdoc />
    public ValueTask<IDirectoryHandler> AttachAsync(
        Identity identity,
        string aname,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IDirectoryHandler>(new DirectoryHandler(registry.Root, this));

    /// <summary>The qid path of <paramref name="node"/>, stable for as long as the server runs.</summary>
    internal ulong QidPathOf(TerminalNode node) => qids.Of(node.Key);

    /// <summary>
    /// The qid of <paramref name="node"/>. The version is the node's own, because a path that
    /// keeps its identity can still be holding something else: <c>stdout</c> grows, <c>status</c>
    /// moves from <c>running</c> to <c>completed</c>, and a client caching on the qid has no
    /// other way to be told.
    /// </summary>
    internal Qid QidOf(TerminalNode node) => new(
        node is TerminalDirectory ? QidType.QTDIR : QidType.QTFILE,
        node.Revision,
        QidPathOf(node));

    /// <summary>Records a refusal that never reached a control session.</summary>
    internal void Refused(string reason) => registry.Refused(reason);

    /// <summary>When <paramref name="node"/> last changed, as the wire carries it.</summary>
    internal TimeSpec TimeOf(TerminalNode node) => node.ModifiedAt is { } when
        ? new TimeSpec(when.ToUnixTimeSeconds(), 0)
        : Started;

    /// <summary>The handler for <paramref name="node"/>, whichever kind it is.</summary>
    internal IHandler HandlerFor(TerminalNode node) => node switch
    {
        TerminalDirectory directory => new DirectoryHandler(directory, this),
        TerminalControl control => new ControlHandler(control, this),
        TerminalKill kill => new KillHandler(kill, this),
        TerminalOutput output => new OutputHandler(output, this),
        TerminalWait wait => new WaitHandler(wait, this),
        TerminalPage page => new PageHandler(page, this),
        _ => throw new NinePException(NinePError.FromErrno(Errno.EIO)),
    };

    /// <summary>
    /// Turns a refusal from the model into one the protocol carries.
    /// </summary>
    /// <remarks>
    /// Both halves travel, because the dialects carry different things: 9P2000 and 9P2000.u carry
    /// the sentence, 9P2000.L carries the number and nothing else. A refusal that reported only
    /// one of them would be mute on half the clients that can reach this tree.
    /// </remarks>
    internal static NinePException Refused(CommandException refusal) =>
        new(new NinePError(refusal.Message, refusal.Errno));
}
