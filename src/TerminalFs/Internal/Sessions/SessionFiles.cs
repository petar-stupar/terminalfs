using TerminalFs.Internal.Mount;

namespace TerminalFs.Internal.Sessions;

/// <summary>
/// The files and directories a session keeps, made private to this user. The records name ports
/// that run commands as them, and a session's directory is where <c>mount</c> is run as root.
/// </summary>
internal static class SessionFiles
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode PrivateDirectory = Private | UnixFileMode.UserExecute;

    /// <summary>Creates or truncates <paramref name="path"/>, readable by this user only.</summary>
    internal static FileStream CreatePrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = Private;
        }

        return new FileStream(path, options);
    }

    /// <summary>
    /// Makes sure <paramref name="root"/> is a real directory, owned by this user and closed to
    /// everyone else.
    /// </summary>
    /// <remarks>
    /// Every other guard here stands on this one. A root somebody else can write to is one they
    /// can plant a session's directory in, as a link to wherever they want root to mount over.
    /// The mode is set rather than checked, and setting it is also the ownership test: only the
    /// owner may change a directory's mode.
    /// </remarks>
    internal static void SecureRoot(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);

            return;
        }

        Directory.CreateDirectory(root, PrivateDirectory);

        if (new DirectoryInfo(root).LinkTarget is { } target)
        {
            throw new MountException($"{root} is a link to {target}; sessions are only kept in a real directory");
        }

        try
        {
            File.SetUnixFileMode(root, PrivateDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            throw new MountException($"{root} belongs to another user; sessions are only kept in a directory of your own");
        }
    }

    /// <summary>
    /// Refuses a session directory that is anything but absent or a plain empty directory, since
    /// it is about to be mounted over as root.
    /// </summary>
    internal static void CheckMountPoint(string path)
    {
        var directory = new DirectoryInfo(path);

        if (directory.LinkTarget is { } target)
        {
            throw new MountException($"{path} is a link to {target}; refusing to mount over it");
        }

        if (File.Exists(path))
        {
            throw new MountException($"{path} is a file; refusing to mount over it");
        }

        if (directory.Exists && directory.EnumerateFileSystemInfos().Any())
        {
            throw new MountException($"{path} is not empty; refusing to mount over it");
        }
    }
}
