namespace TerminalFs.Internal.Mount;

/// <summary>How the served tree is made to appear as a directory on this machine.</summary>
internal enum MountStrategy
{
    /// <summary>
    /// The kernel mounts 9P directly. Linux only: it is the one platform with a v9fs client a
    /// host process can use against a local server.
    /// </summary>
    Native,

    /// <summary>
    /// A container mounts the tree over 9P and re-exports it over SMB, which the host then
    /// mounts. This is what macOS and Windows get, because neither can mount 9P itself without a
    /// third-party kernel extension.
    /// </summary>
    Docker,
}
