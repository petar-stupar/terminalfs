namespace TerminalFs.Internal.Mount;

/// <summary>
/// The container that turns the 9P tree into an SMB share. It mounts 9P with the kernel it has
/// and re-exports it, which is how macOS and Windows reach a tree they cannot mount themselves.
/// </summary>
internal static class DockerBridge
{
    /// <summary>How many times the container's log is checked before giving up on it.</summary>
    private const int Attempts = 30;

    /// <summary>Builds the image and starts the container, replacing one already running.</summary>
    internal static async Task StartAsync(MountSettings settings, CancellationToken cancellationToken = default)
    {
        CommandResult built = await BridgeImage.BuildAsync(cancellationToken).ConfigureAwait(false);

        if (!built.Ok)
        {
            throw new MountException("could not build the bridge image: " + built.Reason);
        }

        await StopAsync(cancellationToken).ConfigureAwait(false);

        CommandResult run = await ProcessRunner.RunAsync(
            "docker",
            [
                "run", "--detach",
                "--name", MountSettings.ContainerName,

                // Mounting a filesystem inside the container is the one privilege the bridge
                // needs, and the narrowest one that allows it.
                "--cap-add", "SYS_ADMIN",

                // The share is published to the loopback interface only. The bridge holds a
                // read-only view of this machine's API documentation and there is no reason for
                // anything off this machine to reach it.
                "--publish", $"127.0.0.1:{settings.SmbPort}:445",
                "--env", $"TERMINALFS_PORT={settings.NinePPort}",
                MountSettings.ImageTag,
            ],
            TimeSpan.FromMinutes(2),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!run.Ok)
        {
            throw new MountException("could not start the bridge container: " + run.Reason);
        }

        await WaitForShareAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the bridge container exists on this machine.</summary>
    internal static async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        CommandResult result = await ProcessRunner.RunAsync(
            "docker",
            ["ps", "--all", "--quiet", "--filter", $"name=^{MountSettings.ContainerName}$"],
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Ok && result.Output.Trim().Length > 0;
    }

    /// <summary>Removes the container if it exists. Quiet when it does not.</summary>
    internal static async Task StopAsync(CancellationToken cancellationToken = default) =>
        await ProcessRunner.RunAsync(
            "docker",
            ["rm", "--force", MountSettings.ContainerName],
            TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Waits until the bridge says it is exporting. The container mounts 9P and starts Samba
    /// before it can serve anything, and a host mount attempted in that window fails for a reason
    /// that has nothing to do with what is wrong.
    /// </summary>
    private static async Task WaitForShareAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            if (attempt > 0 && !await IsRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                // A container that exited will never say it is exporting, and waiting out the
                // whole loop to discover that sends people looking at the network.
                throw new MountException(
                    "the bridge container exited before it started exporting; "
                    + $"'docker logs {MountSettings.ContainerName}' has the reason");
            }

            CommandResult logs = await ProcessRunner.RunAsync(
                "docker",
                ["logs", MountSettings.ContainerName],
                TimeSpan.FromSeconds(15),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            string said = logs.Output + logs.Error;

            if (said.Contains("exporting /srv/terminalfs", StringComparison.Ordinal))
            {
                // Samba binds a moment after it says so.
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

                return;
            }

            if (said.Contains("mount:", StringComparison.Ordinal)
                && said.Contains("failed", StringComparison.Ordinal))
            {
                throw new MountException(
                    "the bridge could not mount the 9P tree; is the server listening on an address the "
                    + "container can reach? " + said.Trim().Split('\n')[^1]);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        throw new MountException(
            $"the bridge container did not start exporting after {Attempts} checks; "
            + $"'docker logs {MountSettings.ContainerName}' has whatever it did say");
    }
}
