using System.Runtime.InteropServices;

namespace TerminalFs.Internal.Mount;

/// <summary>
/// Carries out a mount and then proves it worked. The proof is the point: a mount command that
/// returns success while the directory is unreadable is the worst outcome, because it looks like
/// the tool is broken rather than like something needs permission.
/// </summary>
internal static class Mounter
{
    /// <summary>The two errno values that mean "the system refused", as .NET reports them.</summary>
    private const int Eperm = 1;
    private const int Eacces = 13;

    /// <summary>Mounts the tree, reporting each step as it happens.</summary>
    internal static async Task MountAsync(MountSettings settings, CancellationToken cancellationToken = default)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new MountException(
                "Windows cannot mount this tree directly. Run terminalfs inside WSL and mount there:\n"
                + $"  sudo mount -t 9p -o trans=tcp,port={settings.NinePPort},version=9p2000.L 127.0.0.1 /mnt/terminalfs\n"
                + "Windows then reads it at \\\\wsl$\\<distro>\\mnt\\terminalfs.");
        }

        IReadOnlyList<string> problems = await Preflight.CheckAsync(settings, cancellationToken).ConfigureAwait(false);

        if (problems.Count > 0)
        {
            throw new MountException("this machine cannot mount the tree yet:\n  " + string.Join("\n  ", problems));
        }

        bool bridged = false;

        if (settings.Strategy == MountStrategy.Docker)
        {
            Console.WriteLine("starting the bridge container");
            await DockerBridge.StartAsync(settings, cancellationToken).ConfigureAwait(false);
            bridged = true;
        }

        try
        {
            await HostMount.MountAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MountException or OperationCanceledException)
        {
            // The container was started by this call, so it is this call's to take down again —
            // whether the mount failed or the user pressed Ctrl-C part way through. Leaving it
            // running gives them something they did not ask to keep and no message saying so.
            if (bridged)
            {
                await DockerBridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
                Console.Error.WriteLine("the bridge container was removed again");
            }

            throw;
        }

        Console.WriteLine($"mounted at {settings.MountPath}");

        await VerifyAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches the tree and takes the bridge down with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is mounted decides, not what the flags say. <c>--mount-docker</c> is the only way to
    /// ask for the bridge, and on Linux a later <c>--unmount</c> without it reports the native
    /// strategy: the share was detached and the container left running, which is not what the
    /// README promises and not what the user asked for.
    /// </para>
    /// <para>
    /// The bridge is removed only when this call actually unmounted something through it. The
    /// container is shared by whatever is mounted through it, so tearing it down after being
    /// handed a path that was not ours would break a working mount somewhere else on the strength
    /// of a typo.
    /// </para>
    /// </remarks>
    internal static async Task UnmountAsync(MountSettings settings, CancellationToken cancellationToken = default)
    {
        MountStrategy? mounted = await HostMount.IdentifyAsync(settings, cancellationToken).ConfigureAwait(false);

        if (mounted is null)
        {
            Console.WriteLine($"{settings.MountPath} is not a terminalfs mount; nothing to unmount");

            if (await DockerBridge.IsRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                Console.WriteLine(
                    $"the bridge container {MountSettings.ContainerName} is still running and was left alone");
            }

            return;
        }

        await HostMount.UnmountAsync(settings, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"unmounted {settings.MountPath}");

        if (mounted == MountStrategy.Docker)
        {
            await DockerBridge.StopAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine("bridge container removed");
        }
    }

    /// <summary>
    /// Reads one page through the mount. Anything that comes back is the answer to "did this
    /// work", and the one failure worth explaining in detail is the one this cannot fix itself.
    /// </summary>
    private static async Task VerifyAsync(MountSettings settings, CancellationToken cancellationToken)
    {
        string probe = Path.Combine(settings.MountPath, "index.md");

        // A read on a wedged network mount blocks for as long as the client's own timeout, which
        // on SMB is minutes. Without a deadline this method sits silently after printing
        // "mounted at ...", which looks exactly like the verification having quietly passed —
        // the one outcome it exists to rule out.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            string text = await File.ReadAllTextAsync(probe, deadline.Token).ConfigureAwait(false);

            Console.WriteLine(text.StartsWith("---", StringComparison.Ordinal)
                ? $"verified: {probe} reads back as the catalog index"
                : $"warning: {probe} read back but does not look like the catalog index");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"warning: mounted at {settings.MountPath}, but reading {probe} did not return "
                + "within 15 seconds; the mount may be wedged. 'terminalfs --unmount' clears it up.");
        }
        catch (Exception exception) when (IsPermissionDenied(exception))
        {
            Console.Error.WriteLine(Consent(settings));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warning: mounted, but {probe} could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Whether the failure is the operating system refusing access rather than the tree being
    /// wrong. On macOS this is the one that matters, and it is silent: there is no prompt in a
    /// terminal that was not launched from the GUI, and no entry in the log to find afterwards.
    /// </summary>
    private static bool IsPermissionDenied(Exception exception) => exception switch
    {
        UnauthorizedAccessException => true,

        // The errno first. A message match is a match against strerror(3), which the C library
        // translates: under a non-English LANG the one instruction a macOS user needs would be
        // replaced by the generic line below it. The text is kept as a fallback because the
        // errno a network filesystem surfaces is not always the one that was refused.
        IOException io => io.HResult is Eperm or Eacces
            || io.Message.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
            || io.Message.Contains("denied", StringComparison.OrdinalIgnoreCase),

        _ => false,
    };

    private static string Consent(MountSettings settings)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"warning: mounted at {settings.MountPath}, but reading it was refused by the operating system";
        }

        return $"""
            warning: mounted at {settings.MountPath}, but macOS refused to read it.

            macOS gates access to network volumes per application, and an SMB mount is a network
            volume. Grant it to the program you are running this from:

              System Settings -> Privacy & Security -> Files and Folders
              -> your terminal (or editor) -> enable "Network Volumes"

            A terminal launched from the Finder is asked once, with a dialog. One started by
            another program is refused without a prompt, which is why this reads as a broken
            mount rather than a missing permission.

            The mount is left in place. Grant the permission and read it again; nothing needs to
            be remounted.
            """;
    }
}
