using System.Runtime.InteropServices;

namespace TerminalFs.Internal.Sessions;

/// <summary>
/// Lists a directory with <c>readdir(3)</c> and nothing else: each entry's name and the type the
/// directory itself records for it.
/// </summary>
/// <remarks>
/// <para>
/// .NET's enumeration trusts that type only for directories and regular files. For a link it
/// stats the link's target to decide whether it is a directory, and on a filesystem that records
/// no types at all it stats every entry. Either walks into whatever is mounted there, and a
/// session's server that has stopped answering holds that walk, uninterruptibly, for as long as
/// it does.
/// </para>
/// <para>
/// Linux only, and only where <c>struct dirent</c> has the 64-bit layout glibc and musl share:
/// <c>d_type</c> at byte 18 and the name from byte 19. Sessions are only started on Linux.
/// </para>
/// </remarks>
internal static class RawDirectory
{
    /// <summary>What <c>d_type</c> says.</summary>
    internal const byte Unknown = 0;

    /// <summary>A directory.</summary>
    internal const byte Directory = 4;

    /// <summary>A regular file.</summary>
    internal const byte Regular = 8;

    /// <summary>A symbolic link.</summary>
    internal const byte Link = 10;

    private const int TypeOffset = 18;
    private const int NameOffset = 19;

    /// <summary>Whether this platform's <c>struct dirent</c> is the one read here.</summary>
    internal static bool Supported => OperatingSystem.IsLinux() && IntPtr.Size == 8;

    /// <summary>Every entry in <paramref name="path"/> but <c>.</c> and <c>..</c>.</summary>
    internal static List<(string Name, byte Type)> Read(string path)
    {
        IntPtr name = Marshal.StringToCoTaskMemUTF8(path);

        try
        {
            IntPtr directory = OpenDirectory(name);

            if (directory == IntPtr.Zero)
            {
                throw new IOException($"cannot list {path}: error {Marshal.GetLastPInvokeError()}");
            }

            try
            {
                var entries = new List<(string, byte)>();

                for (IntPtr entry = ReadDirectory(directory); entry != IntPtr.Zero; entry = ReadDirectory(directory))
                {
                    string? entryName = Marshal.PtrToStringUTF8(entry + NameOffset);

                    if (entryName is not (null or "." or ".."))
                    {
                        entries.Add((entryName, Marshal.ReadByte(entry, TypeOffset)));
                    }
                }

                return entries;
            }
            finally
            {
                _ = CloseDirectory(directory);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    // DllImport rather than LibraryImport, whose generated stubs need the project to allow unsafe
    // code; every argument here is a pointer, so there is nothing to marshal either way.
    [DllImport("libc", EntryPoint = "opendir", SetLastError = true)]
    private static extern IntPtr OpenDirectory(IntPtr name);

    [DllImport("libc", EntryPoint = "readdir")]
    private static extern IntPtr ReadDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir")]
    private static extern int CloseDirectory(IntPtr directory);
}
