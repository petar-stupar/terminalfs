using System.Runtime.InteropServices;

namespace TerminalFs.Internal.Mount;

/// <summary>
/// Checks that the machine can do what a mount command is about to ask of it, and says what is
/// missing before anything is started. Every message names the thing to install or enable: a
/// mount that fails halfway is harder to understand than one that never began.
/// </summary>
internal static class Preflight
{
    /// <summary>Everything standing in the way, or an empty list when nothing is.</summary>
    internal static async Task<IReadOnlyList<string>> CheckAsync(
        MountSettings settings,
        CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();

        if (settings.Strategy == MountStrategy.Docker)
        {
            await CheckDockerAsync(problems, cancellationToken).ConfigureAwait(false);
            await CheckHostSmbClientAsync(problems, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CheckNativeAsync(problems, cancellationToken).ConfigureAwait(false);
        }

        return problems;
    }

    private static async Task CheckDockerAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (!ProcessRunner.Exists("docker"))
        {
            problems.Add("docker is not installed; install Docker Desktop, or use --listen and mount by hand");
            return;
        }

        CommandResult info = await ProcessRunner.RunAsync(
            "docker",
            ["info", "--format", "{{.ServerVersion}}"],
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!info.Ok)
        {
            problems.Add("the docker daemon is not responding; start Docker Desktop and try again");
            return;
        }

        // The bridge mounts 9P inside the container, so the container's kernel needs v9fs. Docker
        // Desktop's linuxkit kernel has it built in, but a remote or custom daemon may not, and
        // finding out at mount time reads as a broken tool rather than a missing feature.
        //
        // The question is asked of the bridge image, which is about to be built anyway, rather
        // than of a separate alpine:3 — which was a registry pull on every single mount, and one
        // this project does not otherwise need. If the image is not built yet the check is
        // skipped: a kernel without 9p then fails at the mount, where the bridge says so.
        if (!await BridgeImage.ExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CommandResult filesystems = await ProcessRunner.RunAsync(
            "docker",
            ["run", "--rm", "--entrypoint", "cat", MountSettings.ImageTag, "/proc/filesystems"],
            TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (filesystems.Ok && !filesystems.Output.Contains("9p", StringComparison.Ordinal))
        {
            problems.Add(
                "the docker daemon's kernel has no 9p filesystem, so the bridge cannot mount the tree; "
                + "this needs a daemon whose kernel carries v9fs");
        }
    }

    private static async Task CheckHostSmbClientAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (!File.Exists("/sbin/mount_smbfs"))
            {
                problems.Add("/sbin/mount_smbfs is missing, so the host cannot mount the bridge's share");
            }

            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !ProcessRunner.Exists("mount.cifs"))
        {
            problems.Add("mount.cifs is missing; install cifs-utils, or drop --mount-docker to mount 9P directly");
        }
    }

    /// <summary>
    /// Whether the 9p modules are installed even though nothing has loaded them yet. The
    /// transport this tool uses is <c>trans=tcp</c>, so <c>9pnet_tcp</c> is the one that matters;
    /// <c>9pnet_virtio</c> is for a guest talking to its hypervisor and is not what is wanted
    /// here.
    /// </summary>
    private static bool ModuleAvailable()
    {
        try
        {
            string release = Environment.OSVersion.Version.ToString();
            string modules = Path.Combine("/lib/modules", release, "kernel/fs/9p");

            return Directory.Exists(modules) || Directory.Exists("/lib/modules");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static async Task CheckNativeAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (!File.Exists("/proc/filesystems"))
        {
            problems.Add("this is not a Linux kernel, so 9P cannot be mounted natively; use --mount-docker");
            return;
        }

        string filesystems = await File.ReadAllTextAsync("/proc/filesystems", cancellationToken).ConfigureAwait(false);

        // /proc/filesystems lists what is *loaded*, not what is available. On Debian and Ubuntu
        // 9p is a module that mount(8) autoloads on first use, so refusing here turned an
        // untouched machine that would have mounted perfectly into "the kernel has no 9p
        // filesystem". The modules directory is the second question, and if neither answers, the
        // mount is allowed to try and report the kernel's own words.
        if (!filesystems.Contains("9p", StringComparison.Ordinal) && !ModuleAvailable())
        {
            problems.Add(
                "the kernel has no 9p filesystem and no 9p module to load; on most distributions "
                + "this is the 9pnet_tcp and 9p modules, or use --mount-docker");
        }

        if (!Environment.IsPrivilegedProcess && !ProcessRunner.Exists("sudo"))
        {
            problems.Add("mounting 9P needs root and sudo is not available; run as root or use --mount-docker");
        }
    }
}
