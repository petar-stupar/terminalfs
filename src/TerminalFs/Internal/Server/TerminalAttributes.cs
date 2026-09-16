using NineP.Protocol;

namespace TerminalFs.Internal.Server;

/// <summary>
/// The attributes every node of this tree reports.
/// </summary>
internal static class TerminalAttributes
{
    /// <summary>0555: a directory anyone may list and walk, and nobody may change.</summary>
    internal const FilePermissions DirectoryMode =
        FilePermissions.OwnerReadExecute | FilePermissions.GroupReadExecute | FilePermissions.OtherReadExecute;

    /// <summary>
    /// 0777: a directory a caller may remove things from.
    /// </summary>
    /// <remarks>
    /// The write bit is what makes <c>rm -r /cmd/&lt;id&gt;</c> possible at all: removing
    /// something is judged against the permissions of the directory it is in, not of the thing
    /// being removed, and without it the client refuses locally and nothing ever reaches this
    /// server to be considered. It is open to all because a bridge writes as <c>nobody</c>
    /// rather than as this tree's owner.
    /// </remarks>
    internal const FilePermissions WritableDirectoryMode =
        FilePermissions.AllRead | FilePermissions.AllWrite | FilePermissions.AllExecute;

    /// <summary>0444: a file anyone may read and nobody may write.</summary>
    internal const FilePermissions PageMode = FilePermissions.AllRead;

    /// <summary>
    /// 0666: the control file, which takes commands and describes itself.
    /// </summary>
    /// <remarks>
    /// The read bit is not decoration. <c>Txattrwalk</c> requires read permission before the
    /// server is even asked whether it has extended attributes, and Samba asks for them when it
    /// opens a file — so a write-only control file cannot be opened through an SMB bridge at all,
    /// and the refusal surfaces as "permission denied" on <c>echo … &gt; ctl</c>.
    /// </remarks>
    internal const FilePermissions ControlMode = FilePermissions.AllRead | FilePermissions.AllWrite;

    /// <summary>
    /// The attributes of one node.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner is stated rather than left unknown. <c>Attr.Uid</c> defaults to
    /// <c>NONUNAME</c>, which is the right sentinel for 9P2000 and 9P2000.u, where ownership
    /// travels as a name. In 9P2000.L it is a number the client must map, and Linux cannot map
    /// that one: it substitutes the overflow uid and answers <c>EOVERFLOW</c> to anything that
    /// needs the real owner — so an <c>rm</c> fails locally, without a message ever reaching this
    /// server to be refused honestly.
    /// </para>
    /// <para>
    /// Unlike a tree of generated documentation, the times here move: a command's files change
    /// while a caller is watching them, and a client that caches on an unchanged mtime would go
    /// on showing output that has already grown. Each node says when it last changed, and only a
    /// node that never changes reports the time the server started.
    /// </para>
    /// </remarks>
    internal static Attr Of(Qid qid, FileKind kind, FilePermissions mode, ulong size, TimeSpec when) => new()
    {
        Qid = qid,
        Kind = kind,
        Perm = mode,
        Size = size,
        Uid = 0,
        Gid = 0,
        UserName = "root",
        GroupName = "root",
        ModifierUid = 0,
        ModifierName = "root",
        ATime = when,
        MTime = when,
        CTime = when,
        BTime = when,
    };
}
