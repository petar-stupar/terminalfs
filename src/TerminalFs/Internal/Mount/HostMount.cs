using System.Runtime.InteropServices;

namespace TerminalFs.Internal.Mount;

/// <summary>
/// Attaches and detaches the tree at a directory on this machine, through whichever mount command
/// the platform has.
/// </summary>
internal static class HostMount
{
    /// <summary>Mounts the tree at <see cref="MountSettings.MountPath"/>.</summary>
    internal static async Task MountAsync(MountSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(settings.MountPath);

        CommandResult result = settings.Strategy switch
        {
            MountStrategy.Native => await NativeAsync(settings, cancellationToken).ConfigureAwait(false),
            _ => await SmbAsync(settings, cancellationToken).ConfigureAwait(false),
        };

        if (!result.Ok)
        {
            throw new MountException($"could not mount at {settings.MountPath}: {result.Reason}");
        }
    }

    /// <summary>Linux mounts the 9P server itself; nothing sits in between.</summary>
    private static Task<CommandResult> NativeAsync(MountSettings settings, CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "mount", "-t", "9p",
            // Not "ro": that would refuse a write to /ctl in the kernel, and /ctl is the one
            // writable file here. What keeps the documentation read-only is the tree's own
            // permissions, which the server enforces. uname and dfltuid pin the attach identity
            // to the owner the server reports, without which /ctl cannot be written at all.
            // cache=none for the reason spelled out in BridgeImage: a caching mode pins a fid per
            // cached dentry, and this tree has more directories than the server's per-connection
            // fid cap, so a recursive walk wedges the mount with ENFILE until it is remade.
            "-o", $"trans=tcp,port={settings.NinePPort},version=9p2000.L,msize=262144,cache=none,uname=root,dfltuid=0,access=any",
            "127.0.0.1", settings.MountPath,
        ];

        // Mounting needs root. Running as root already, sudo would be a needless dependency.
        return Privileged(arguments, TimeSpan.FromSeconds(60), cancellationToken);
    }

    /// <summary>
    /// Runs a command as root, through <c>sudo</c> only when this process is not root already.
    /// </summary>
    /// <remarks>
    /// The question is asked of the process and not of <c>$USER</c>, which is unset under systemd,
    /// cron and most containers — sending root through a <c>sudo</c> a minimal image does not have
    /// — and is caller-controlled the other way, so <c>USER=root terminalfs --mount</c> skipped
    /// sudo and failed with whatever mount(8) says about permissions.
    /// </remarks>
    private static Task<CommandResult> Privileged(
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Environment.IsPrivilegedProcess
            ? ProcessRunner.RunAsync(arguments[0], arguments[1..], timeout, cancellationToken: cancellationToken)
            : ProcessRunner.RunAsync("sudo", arguments, timeout, cancellationToken: cancellationToken);

    /// <summary>
    /// The effective user id, asked of the system rather than read from the environment.
    /// <c>UID</c> is a shell variable and is not exported, so reading it yields null on almost
    /// every machine — and the fallback then hands the mount to whoever happens to be uid 1000.
    /// </summary>
    private static async Task<string> CurrentUidAsync(CancellationToken cancellationToken)
    {
        CommandResult result = await ProcessRunner.RunAsync(
            "id",
            ["-u"],
            TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string uid = result.Output.Trim();

        return result.Ok && uid.Length > 0 ? uid : "0";
    }

    private static async Task<CommandResult> SmbAsync(MountSettings settings, CancellationToken cancellationToken)
    {
        string share = $"//guest:@127.0.0.1:{settings.SmbPort}/{MountSettings.ShareName}";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // mount_smbfs mounts as the calling user, so this needs no privilege at all.
            return await ProcessRunner.RunAsync(
                "/sbin/mount_smbfs",
                [share, settings.MountPath],
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        string uid = await CurrentUidAsync(cancellationToken).ConfigureAwait(false);

        // Through Privileged like the native path, not a bare sudo: as root in a container with
        // no sudo installed, this failed with "sudo: not found" on a machine that could mount.
        return await Privileged(
            [
                "mount", "-t", "cifs",
                $"//127.0.0.1/{MountSettings.ShareName}", settings.MountPath,
                "-o", $"port={settings.SmbPort},guest,uid={uid}",
            ],
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches the mount at <see cref="MountSettings.MountPath"/>. Refuses a path that is not one
    /// of ours, because <c>--unmount --path</c> takes a directory from the command line and
    /// detaching the wrong one is not something a user can undo.
    /// </summary>
    internal static async Task UnmountAsync(MountSettings settings, CancellationToken cancellationToken = default)
    {
        string full = Path.GetFullPath(settings.MountPath);

        if (await IdentifyAsync(settings, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new MountException(
                $"{full} is not a terminalfs mount; refusing to unmount it");
        }

        // Undone by the privilege that did it. On Linux both mounts are made through sudo, and a
        // mount made by root is not detachable by anyone else without a fstab entry saying so —
        // so a plain umount here failed both --unmount and the automatic unmount on the way out,
        // leaving exactly the orphaned mount the signal handling exists to avoid. macOS needs
        // none of this: mount_smbfs mounts as the calling user and umount undoes it as the same.
        CommandResult result = OperatingSystem.IsMacOS()
            ? await ProcessRunner.RunAsync(
                "umount",
                [full],
                TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken).ConfigureAwait(false)
            : await Privileged(["umount", full], TimeSpan.FromSeconds(60), cancellationToken)
                .ConfigureAwait(false);

        if (!result.Ok)
        {
            throw new MountException($"could not unmount {full}: {result.Reason}");
        }
    }

    /// <summary>
    /// How the tree at <see cref="MountSettings.MountPath"/> got there, or null when whatever is
    /// mounted there is not ours.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test is the <em>device</em> this tool would have written, not the filesystem type.
    /// "Anything of type 9p at this path" is not a test at all on the machines this project sends
    /// people to: inside WSL every Windows drive is a 9p mount, so
    /// <c>terminalfs --unmount --path /mnt/c</c> passed the guard and ran <c>umount /mnt/c</c>.
    /// A 9P mount of ours names <c>127.0.0.1</c> and carries <c>trans=tcp</c> with our port; a
    /// bridge mount names our share on loopback. WSL's drives name <c>C:\</c> and
    /// <c>trans=fd</c>, and match neither.
    /// </para>
    /// <para>
    /// The answer also says which half of the bridge is in play, because the flags cannot: on
    /// Linux, <c>--unmount</c> without <c>--mount-docker</c> reports the native strategy even when
    /// what is mounted is the container's share, and would then leave the container running.
    /// </para>
    /// </remarks>
    internal static async Task<MountStrategy?> IdentifyAsync(
        MountSettings settings,
        CancellationToken cancellationToken = default)
    {
        CommandResult mounts = await ProcessRunner.RunAsync(
            "mount",
            [],
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!mounts.Ok)
        {
            return null;
        }

        return Identify(mounts.Output.Split('\n'), settings);
    }

    /// <summary>
    /// The same question asked of mount table lines that are already in hand, which is the whole
    /// of the decision and the part worth pinning with a test.
    /// </summary>
    internal static MountStrategy? Identify(IEnumerable<string> lines, MountSettings settings)
    {
        string full = Path.GetFullPath(settings.MountPath);
        string real = RealPath(full);

        foreach (string line in lines)
        {
            string? point = MountPointOf(line);

            if (point is null || (point != full && point != real && RealPath(point) != real))
            {
                continue;
            }

            string device = DeviceOf(line);

            // The bridge: an SMB or CIFS mount of our share, on loopback.
            if (device.Contains('/' + MountSettings.ShareName, StringComparison.Ordinal)
                && device.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                return MountStrategy.Docker;
            }

            // A direct 9P mount: our device, our transport, our port.
            if (device == "127.0.0.1"
                && line.Contains("trans=tcp", StringComparison.Ordinal)
                && line.Contains(
                    "port=" + settings.NinePPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                return MountStrategy.Native;
            }

            return null;
        }

        return null;
    }

    /// <summary>The device column of one line of <c>mount(8)</c>: everything before " on ".</summary>
    private static string DeviceOf(string line)
    {
        int on = line.IndexOf(" on ", StringComparison.Ordinal);

        return on < 0 ? string.Empty : Unescape(line[..on]);
    }

    /// <summary>
    /// <paramref name="path"/> with every symbolic link in it resolved, as the kernel sees it.
    /// </summary>
    /// <remarks>
    /// The mount table records the kernel's path. <c>Path.GetFullPath</c> resolves <c>..</c> and
    /// makes a path absolute but follows no links, so on a system where <c>/home</c> is a link to
    /// <c>/var/home</c> the mount this tool had just made was recorded under a name the tool's own
    /// guard did not recognise, and <c>--unmount</c> refused it.
    /// </remarks>
    private static string RealPath(string path)
    {
        try
        {
            string at = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(at);
            var resolved = new List<string>();

            foreach (string segment in at[(root?.Length ?? 0)..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                resolved.Add(segment);
                string sofar = Path.Combine([root ?? string.Empty, .. resolved]);

                if (Directory.ResolveLinkTarget(sofar, returnFinalTarget: true) is { } target)
                {
                    resolved.Clear();
                    root = target.FullName + Path.DirectorySeparatorChar;
                }
            }

            return Path.Combine([root ?? string.Empty, .. resolved]).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>
    /// The mount point one line of <c>mount(8)</c> describes, or null.
    /// </summary>
    /// <remarks>
    /// Both platforms write <c>&lt;device&gt; on &lt;mount point&gt; &lt;the rest&gt;</c>, so the
    /// point is what lies between " on " and either " type " (Linux) or " (" (macOS). It is
    /// compared for equality rather than searched for, because this guards an unmount taken from
    /// the command line: a containment test also matches the device column, and no test at all
    /// matched a path with a space in it, which Linux escapes as <c>\040</c> — so a mount point
    /// under "My Documents" could be created by this tool and then not detached by it.
    /// </remarks>
    private static string? MountPointOf(string line)
    {
        int on = line.IndexOf(" on ", StringComparison.Ordinal);

        if (on < 0)
        {
            return null;
        }

        int start = on + " on ".Length;
        int end = line.IndexOf(" type ", start, StringComparison.Ordinal);

        if (end < 0)
        {
            end = line.IndexOf(" (", start, StringComparison.Ordinal);
        }

        if (end < 0)
        {
            end = line.Length;
        }

        return Unescape(line[start..end]);
    }

    /// <summary>Undoes the octal escapes Linux writes for a space, tab, newline or backslash.</summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\', StringComparison.Ordinal))
        {
            return field;
        }

        var builder = new System.Text.StringBuilder(field.Length);

        for (int at = 0; at < field.Length; at++)
        {
            if (field[at] == '\\' && at + 3 < field.Length
                && int.TryParse(field.AsSpan(at + 1, 3), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int octal))
            {
                builder.Append((char)Convert.ToInt32(octal.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), 8));
                at += 3;

                continue;
            }

            builder.Append(field[at]);
        }

        return builder.ToString();
    }
}
