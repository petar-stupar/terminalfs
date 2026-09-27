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
    public void EachEntryIsNamedWithItsKindAndALinkIsNotFollowed()
    {
        // Making a link on Windows needs a privilege CI does not have, and sessions are not
        // started there.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "links need a privilege on Windows");

        Directory.CreateDirectory(Path.Combine(root, "tree"));
        File.WriteAllText(Path.Combine(root, "tree.session"), "{}");
        Directory.CreateSymbolicLink(Path.Combine(root, "pointer"), Path.Combine(root, "tree"));
        File.CreateSymbolicLink(Path.Combine(root, "dangling"), Path.Combine(root, "nowhere"));

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

        List<(string Name, byte Type)> entries = RawDirectory.Read(root);

        Assert.Contains(("a", RawDirectory.Directory), entries);
        Assert.Contains(("b", RawDirectory.Link), entries);
        Assert.DoesNotContain(entries, entry => entry.Name is "." or "..");
    }

    [Fact]
    public void AMissingRootListsNothing() =>
        Assert.Empty(SessionFiles.List(Path.Combine(root, "absent")));

    public void Dispose() => Directory.Delete(root, recursive: true);
}
