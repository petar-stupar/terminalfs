using TerminalFs.Internal.Mount;
using Xunit;

namespace TerminalFs.Tests;

/// <summary>
/// The predicate behind <c>--unmount</c>. This is the only destructive thing this program does
/// and the path comes from the command line, so what it accepts is pinned here with real
/// <c>mount(8)</c> output from each platform it runs on.
/// </summary>
public class MountGuardTests
{
    private static readonly MountSettings Settings =
        new("/home/me/mnt/terminalfs", MountStrategy.Native, NinePPort: 15641, SmbPort: 14451);

    [Fact]
    public void ADirect9PMountOfOursIsRecognised() =>
        Assert.Equal(
            MountStrategy.Native,
            HostMount.Identify(
                [
                    "127.0.0.1 on /home/me/mnt/terminalfs type 9p "
                        + "(rw,relatime,sync,dirsync,access=any,trans=tcp,port=15641)",
                ],
                Settings));

    [Fact]
    public void TheBridgesShareIsRecognisedOnLinux() =>
        Assert.Equal(
            MountStrategy.Docker,
            HostMount.Identify(
                ["//127.0.0.1/terminalfs on /home/me/mnt/terminalfs type cifs (rw,relatime,vers=3.1.1)"],
                Settings));

    [Fact]
    public void TheBridgesShareIsRecognisedOnMacOs() =>
        Assert.Equal(
            MountStrategy.Docker,
            HostMount.Identify(
                ["//guest@127.0.0.1:14451/terminalfs on /home/me/mnt/terminalfs (smbfs, nodev, nosuid, read-only)"],
                Settings));

    /// <summary>
    /// The one that mattered. Inside WSL every Windows drive is a 9p mount, and the README sends
    /// Windows users to WSL: <c>--unmount --path /mnt/c</c> passed a guard that asked only
    /// whether the type was 9p, and ran <c>umount /mnt/c</c>.
    /// </summary>
    [Fact]
    public void AWindowsDriveInsideWslIsNotOurs() =>
        Assert.Null(HostMount.Identify(
            ["C:\\ on /mnt/c type 9p (rw,noatime,dirsync,aname=drvfs;path=C:\\;uid=1000,mmap,access=client,msize=262144,trans=fd,rfdno=4,wfdno=4)"],
            Settings with { MountPath = "/mnt/c" }));

    [Fact]
    public void A9PMountOnSomeOtherPortIsNotOurs() =>
        Assert.Null(HostMount.Identify(
            ["127.0.0.1 on /home/me/mnt/terminalfs type 9p (rw,trans=tcp,port=564)"],
            Settings));

    [Fact]
    public void AnOrdinaryFilesystemAtThePathIsNotOurs() =>
        Assert.Null(HostMount.Identify(
            ["/dev/nvme0n1p2 on /home/me/mnt/terminalfs type ext4 (rw,relatime)"],
            Settings));

    [Fact]
    public void NothingMountedAtThePathIsNotOurs() =>
        Assert.Null(HostMount.Identify(
            ["127.0.0.1 on /home/me/mnt/elsewhere type 9p (rw,trans=tcp,port=15640)"],
            Settings));

    /// <summary>
    /// A mount point with a space in it. Linux writes it escaped, and a guard that did not undo
    /// the escape refused to detach a mount this tool had just made.
    /// </summary>
    [Fact]
    public void AMountPointWithASpaceIsRecognised() =>
        Assert.Equal(
            MountStrategy.Native,
            HostMount.Identify(
                ["127.0.0.1 on /home/me/My\\040Documents/docs type 9p (rw,trans=tcp,port=15641)"],
                Settings with { MountPath = "/home/me/My Documents/docs" }));

    /// <summary>A port is read whole: a test for 1564 must not match a mount of 15641.</summary>
    [Fact]
    public void APortThatOursIsAPrefixOfIsNotOurs() =>
        Assert.Null(HostMount.Identify(
            ["127.0.0.1 on /home/me/mnt/terminalfs type 9p (rw,trans=tcp,port=156410)"],
            Settings));

    /// <summary>
    /// A session whose record was never written is found by where it is mounted, and only a
    /// direct loopback 9P mount there counts.
    /// </summary>
    [Fact]
    public void TheLoopback9PMountAtAPathNamesItsPort()
    {
        string[] table =
        [
            "127.0.0.1 on /run/user/1000/terminalfs/a type 9p (rw,relatime,dfltuid=0,uname=root,access=any,msize=262144,trans=tcp,port=42855)",
            "//127.0.0.1/terminalfs on /run/user/1000/terminalfs/b type cifs (rw)",
            "C:\\ on /run/user/1000/terminalfs/c type 9p (rw,trans=fd,rfdno=4,wfdno=4)",
        ];

        Assert.Equal(42855, HostMount.NinePPortAt(table, "/run/user/1000/terminalfs/a"));
        Assert.Null(HostMount.NinePPortAt(table, "/run/user/1000/terminalfs/b"));
        Assert.Null(HostMount.NinePPortAt(table, "/run/user/1000/terminalfs/c"));
        Assert.Null(HostMount.NinePPortAt(table, "/run/user/1000/terminalfs/d"));
    }

    /// <summary>
    /// A session's directory is only looked into once the table says nothing is mounted on it, so
    /// a table that could not be read must never read as an empty one.
    /// </summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    public void AMountTableThatCouldNotBeReadIsAnErrorNotAnEmptyTable(int exitCode, bool missing)
    {
        MountException refused = Assert.Throws<MountException>(
            () => HostMount.MountTableFrom(new CommandResult("mount", exitCode, string.Empty, "mount: failed", missing)));

        Assert.Contains("cannot read the mount table", refused.Message, StringComparison.Ordinal);
        Assert.Equal(
            ["127.0.0.1 on /a type 9p (trans=tcp,port=1)"],
            HostMount.MountTableFrom(new CommandResult("mount", 0, "127.0.0.1 on /a type 9p (trans=tcp,port=1)", string.Empty)));
    }

    /// <summary>
    /// A link at a session's path does not answer for the mount it points at, or stopping one
    /// session would unmount another's.
    /// </summary>
    [Fact]
    public void ALinkToAMountIsNotThatMount()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the mount table is a Unix one");

        string root = Path.Combine(Path.GetTempPath(), "terminalfs-guard-" + Guid.NewGuid().ToString("N"));
        string real = Path.Combine(root, "a");
        string link = Path.Combine(root, "b");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);

        try
        {
            string[] table = [$"127.0.0.1 on {real} type 9p (rw,trans=tcp,port=42855)"];

            Assert.Equal(42855, HostMount.NinePPortAt(table, real));
            Assert.Null(HostMount.NinePPortAt(table, link));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The device column must not be able to satisfy the mount-point test.</summary>
    [Fact]
    public void ADeviceNamedLikeOurMountPointIsNotAMatch() =>
        Assert.Null(HostMount.Identify(
            ["/home/me/mnt/terminalfs on /mnt/somewhere-else type ext4 (rw)"],
            Settings));
}
