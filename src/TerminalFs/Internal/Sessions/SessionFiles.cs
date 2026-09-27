using TerminalFs.Internal.Mount;

namespace TerminalFs.Internal.Sessions;

/// <summary>What an entry in the runtime directory is, as its listing says.</summary>
internal enum EntryKind
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>
    /// A FIFO, socket or device. Never opened: opening a FIFO blocks until somebody writes to
    /// it, and nothing here should wait on anyone.
    /// </summary>
    Other,

    /// <summary>A directory: a session's mount point, mounted or not.</summary>
    Directory,

    /// <summary>A symbolic link, which is never followed.</summary>
    Link,

    /// <summary>
    /// The filesystem did not say. Only the mount table can tell whether looking is safe.
    /// </summary>
    Unknown,
}

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
    /// What <paramref name="root"/> holds, by name, read from the directory listing alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing in the root is stat-ed, and that is the point. A session's directory is a mount
    /// point, and stat-ing a mount point asks the server behind it; one that has stopped
    /// answering blocks the caller, uninterruptibly, for as long as it does — and clearing up
    /// such a session is exactly what these callers are for. A dead one answers with an error,
    /// which made its directory read as absent. The listing's own entry types come from the
    /// directory being listed, not from anything mounted in it.
    /// </para>
    /// <para>
    /// An entry the filesystem gives no type for is <see cref="EntryKind.Unknown"/>, for the
    /// caller to settle against the mount table before anything looks at it.
    /// </para>
    /// </remarks>
    internal static Dictionary<string, EntryKind> List(string root)
    {
        var entries = new Dictionary<string, EntryKind>(StringComparer.Ordinal);

        if (!Directory.Exists(root))
        {
            return entries;
        }

        if (RawDirectory.Supported)
        {
            foreach ((string name, byte type) in RawDirectory.Read(root))
            {
                entries[name] = type switch
                {
                    RawDirectory.Directory => EntryKind.Directory,
                    RawDirectory.Link => EntryKind.Link,
                    RawDirectory.Regular => EntryKind.File,
                    RawDirectory.Unknown => EntryKind.Unknown,
                    _ => EntryKind.Other,
                };
            }

            return entries;
        }

        // Elsewhere .NET's own enumeration, which is right for every entry but a link or an
        // untyped one. Sessions are not started there, so nothing is mounted to walk into.
        foreach ((string name, bool directory) in Enumerate(root, skip: 0))
        {
            entries[name] = directory ? EntryKind.Directory : EntryKind.File;
        }

        // A link is told apart by listing again without links, which the listing can do from the
        // entry type it already has.
        var plain = Enumerate(root, skip: FileAttributes.ReparsePoint).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);

        foreach (string name in entries.Keys.Where(name => !plain.Contains(name)).ToList())
        {
            entries[name] = EntryKind.Link;
        }

        return entries;
    }

    private static List<(string Name, bool Directory)> Enumerate(string root, FileAttributes skip) =>
    [
        .. new System.IO.Enumeration.FileSystemEnumerable<(string, bool)>(
            root,
            (ref System.IO.Enumeration.FileSystemEntry entry) => (entry.FileName.ToString(), entry.IsDirectory),
            new EnumerationOptions { AttributesToSkip = skip, IgnoreInaccessible = true, RecurseSubdirectories = false }),
    ];

    /// <summary>
    /// What an entry the filesystem gave no type for is, once the caller knows nothing is mounted
    /// on it: a link is found by reading it as one, which does not follow it, and only then is the
    /// entry itself looked at.
    /// </summary>
    internal static EntryKind Settle(string path)
    {
        try
        {
            if (File.ResolveLinkTarget(path, returnFinalTarget: false) is not null)
            {
                return EntryKind.Link;
            }
        }
        catch (IOException)
        {
            // Gone, or not readable as a link: what follows answers for it.
        }

        return Directory.Exists(path) ? EntryKind.Directory : EntryKind.File;
    }

    /// <summary>
    /// Opens the root's lock, creating it readable by this user only.
    /// </summary>
    /// <remarks>
    /// On Unix an unshared <see cref="FileStream"/> is an advisory <c>flock</c>, so it is
    /// released however its holder ends — including by being killed.
    /// </remarks>
    internal static FileStream OpenLock(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = Private;
        }

        return new FileStream(path, options);
    }

    /// <summary>
    /// Refuses a session directory that is anything but absent or a plain empty directory, since
    /// it is about to be mounted over as root. Only called once the mount table says nothing is
    /// mounted there, because it looks inside.
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
