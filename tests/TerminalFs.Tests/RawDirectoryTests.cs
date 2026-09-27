using TerminalFs.Internal.Sessions;

namespace TerminalFs.Tests;

/// <summary>
/// The listing sessions are cleared up from. It must say what each entry is from the directory
/// alone: stat-ing an entry, or following a link, walks into whatever is mounted there, and a
/// session whose server has stopped answering holds that walk for as long as it does.
/// </summary>
public sealed class RawDirectoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "terminalfs-listing-" + Guid.NewGuid().ToString("N"));

    public RawDirectoryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void EachEntryIsListedWithItsKind()
    {
        // Making a link on Windows needs a privilege CI does not have, and sessions are not
        // started there.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "links need a privilege on Windows");

        Directory.CreateDirectory(Path.Combine(root, "tree"));
        File.WriteAllText(Path.Combine(root, "tree.session"), "{}");
        Directory.CreateSymbolicLink(Path.Combine(root, "pointer"), Path.Combine(root, "tree"));
        File.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(root, "nowhere"));
        SkipWhereTheFilesystemRecordsNoTypes();

        Dictionary<string, EntryKind> listed = SessionFiles.List(root);

        Assert.Equal(EntryKind.Directory, listed["tree"]);
        Assert.Equal(EntryKind.File, listed["tree.session"]);
        Assert.Equal(EntryKind.Link, listed["pointer"]);
        Assert.Equal(EntryKind.Link, listed["dangling"]);
        Assert.Equal(4, listed.Count);
    }

    [Fact]
    public void OnLinuxTheDirectoryItselfIsRead()
    {
        Assert.SkipUnless(RawDirectory.Supported, "struct dirent is only read on 64-bit Linux");

        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateSymbolicLink(Path.Combine(root, "b"), Path.Combine(root, "a"));
        SkipWhereTheFilesystemRecordsNoTypes();

        List<(string Name, byte Type)> entries = RawDirectory.Read(root);

        Assert.Contains(("a", RawDirectory.Directory), entries);
        Assert.Contains(("b", RawDirectory.Link), entries);
        Assert.DoesNotContain(entries, entry => entry.Name is "." or "..");
    }

    /// <summary>
    /// Where the filesystem gives no type, an entry nothing is mounted on is settled by reading
    /// it as a link first, which does not follow it, and only then by looking at it.
    /// </summary>
    [Fact]
    public void AnEntryWithNoTypeIsSettledWithoutFollowingALink()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "links need a privilege on Windows");

        Directory.CreateDirectory(Path.Combine(root, "dir"));
        File.WriteAllText(Path.Combine(root, "file"), "x");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "dir"));

        Assert.Equal(EntryKind.Directory, SessionFiles.Settle(Path.Combine(root, "dir")));
        Assert.Equal(EntryKind.File, SessionFiles.Settle(Path.Combine(root, "file")));
        Assert.Equal(EntryKind.Link, SessionFiles.Settle(Path.Combine(root, "link")));
    }

    [Fact]
    public void AMissingRootListsNothing() =>
        Assert.Empty(SessionFiles.List(Path.Combine(root, "absent")));

    public void Dispose() => Directory.Delete(root, recursive: true);

    /// <summary>
    /// The kinds above come from the filesystem the tests run on. One that records no types
    /// leaves them unknown, which is correct and is what the settling test covers.
    /// </summary>
    private void SkipWhereTheFilesystemRecordsNoTypes() =>
        Assert.SkipWhen(
            RawDirectory.Supported && RawDirectory.Read(root).Any(entry => entry.Type == RawDirectory.Unknown),
            "this filesystem records no entry types");
}
