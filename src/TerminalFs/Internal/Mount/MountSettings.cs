using System.Runtime.InteropServices;

namespace TerminalFs.Internal.Mount;

/// <summary>Everything the mount commands need to agree on.</summary>
/// <param name="MountPath">Where the tree appears on this machine.</param>
/// <param name="Strategy">How it gets there.</param>
/// <param name="NinePPort">The port the 9P server listens on.</param>
/// <param name="SmbPort">The host port the bridge publishes its SMB share on.</param>
internal sealed record MountSettings(
    string MountPath,
    MountStrategy Strategy,
    int NinePPort,
    int SmbPort)
{
    /// <summary>The container the bridge runs in.</summary>
    internal const string ContainerName = "terminalfs-bridge";

    /// <summary>The image the bridge runs, built locally on first use.</summary>
    internal const string ImageTag = "terminalfs/bridge:1";

    /// <summary>The SMB share the bridge exports.</summary>
    internal const string ShareName = "terminalfs";

    /// <summary>Where the tree appears when nobody says otherwise.</summary>
    internal static string DefaultMountPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "mnt",
        "terminalfs");

    /// <summary>
    /// The strategy this machine can actually use. Linux mounts 9P itself unless asked for the
    /// container; everywhere else the container is the only way that does not need a third-party
    /// kernel extension.
    /// </summary>
    internal static MountStrategy StrategyFor(bool dockerRequested) =>
        !dockerRequested && RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? MountStrategy.Native
            : MountStrategy.Docker;
}
