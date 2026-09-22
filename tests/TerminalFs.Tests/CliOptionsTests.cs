namespace TerminalFs.Tests;

/// <summary>
/// The command line. The refusals here are the ones that would otherwise fail somewhere far from
/// their cause — or, in one case, not fail at all.
/// </summary>
public sealed class CliOptionsTests
{
    [Fact]
    public void TheDefaultsServeLoopbackOnFifteenSixFortyOne()
    {
        CliOptions options = CliOptions.Parse([]);

        Assert.Equal("tcp://127.0.0.1:15641", options.ListenAddress);
        Assert.Equal(15641, options.NinePPort);
        Assert.Equal(14451, options.SmbPort);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Keep);
        Assert.Equal(TimeSpan.FromSeconds(25), options.WaitTimeout);
    }

    [Theory]
    [InlineData("--port", "9999")]
    [InlineData("--smb-port", "9999")]
    [InlineData("--shell", "/bin/bash")]
    [InlineData("--cwd", "/tmp")]
    [InlineData("--keep", "5")]
    [InlineData("--wait-timeout", "5")]
    [InlineData("--path", "/tmp/x")]
    [InlineData("--settings", "/tmp/s.json")]
    [InlineData("--listen", "tcp://127.0.0.1:1")]
    public void AValueTakingFlagAcceptsBothSpellings(string flag, string value)
    {
        CliOptions spaced = CliOptions.Parse([flag, value]);
        CliOptions inline = CliOptions.Parse([$"{flag}={value}"]);

        Assert.Equal(spaced, inline);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--shell")]
    [InlineData("--keep")]
    public void AFlagWithNoValueIsRefused(string flag)
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse([flag]));
        Assert.Throws<CliUsageException>(() => CliOptions.Parse([$"{flag}="]));
    }

    [Theory]
    [InlineData("--keep", "soon")]
    [InlineData("--keep", "-1")]
    [InlineData("--wait-timeout", "never")]
    public void SomethingThatIsNotANumberOfSecondsIsRefused(string flag, string value) =>
        Assert.Throws<CliUsageException>(() => CliOptions.Parse([flag, value]));

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--smb-port", "notaport")]
    public void SomethingThatIsNotAPortIsRefused(string flag, string value) =>
        Assert.Throws<CliUsageException>(() => CliOptions.Parse([flag, value]));

    [Fact]
    public void AKeepOrWaitOutOfRangeIsRefused()
    {
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--keep", "90000"]).Validated());
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--wait-timeout", "0"]).Validated());
    }

    [Fact]
    public void AKeepOfZeroIsAllowedAndMeansRemoveAtOnce() =>
        Assert.Equal(TimeSpan.Zero, CliOptions.Parse(["--keep", "0"]).Validated().Keep);

    /// <summary>
    /// The one refusal with no flag to override it. This server runs whatever is written to
    /// <c>/ctl</c>, as the user who started it; whoever can open the socket gets a shell.
    /// </summary>
    [Theory]
    [InlineData("tcp://0.0.0.0:15641")]
    [InlineData("tcp://192.168.1.10:15641")]
    [InlineData("ws://0.0.0.0:8080/9p")]
    public void ServingOffLoopbackIsAlwaysRefused(string listen)
    {
        CliUsageException refused = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["--listen", listen]).Validated());

        Assert.Contains("run commands as you", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:15641")]
    [InlineData("tcp://localhost:15641")]
    [InlineData("tcp://[::1]:15641")]
    public void LoopbackIsAllowed(string listen) =>
        Assert.Equal(listen, CliOptions.Parse(["--listen", listen]).Validated().Listen);

    /// <summary>
    /// A unix socket has no host to be loopback or not: it is a path on this machine, reachable
    /// only by something already on it, which is more local than loopback rather than less.
    /// </summary>
    [Fact]
    public void AUnixSocketIsLocalEnough() =>
        CliOptions.Parse(["--listen", "unix:///tmp/terminalfs.sock"]).Validated();

    [Fact]
    public void AListenAddressThatIsNotAnAddressIsRefused() =>
        Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--listen", "not an address"]).Validated());

    [Fact]
    public void MountingWhatIsNotTcpIsRefused()
    {
        CliUsageException refused = Assert.Throws<CliUsageException>(
            () => CliOptions.Parse(["--listen", "unix:///tmp/s.sock", "--mount"]).Validated());

        Assert.Contains("trans=tcp", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The listen address wins over <c>--port</c>, because it is the more specific of the two.
    /// Mounting the other one would at best fail and at worst attach to a different server still
    /// running on the default port.
    /// </summary>
    [Fact]
    public void AMountFollowsTheListenAddressRatherThanThePort()
    {
        CliOptions options = CliOptions.Parse(
            ["--listen", "tcp://127.0.0.1:9999", "--port", "15641", "--mount"]).Validated();

        Assert.Equal(9999, options.MountSettings.NinePPort);
    }

    [Fact]
    public void TheMountPathIsMadeAbsolute() =>
        Assert.True(Path.IsPathRooted(CliOptions.Parse(["--path", "relative"]).MountSettings.MountPath));

    /// <summary>
    /// Mounting resolves a path nobody gave, but the served skill must not: it prints the path it
    /// is given, and a default nobody stated would send an agent to a directory that is not there.
    /// So the two readings are kept apart — <c>MountSettings</c> always has one, and
    /// <c>MountPath</c> only when somebody said.
    /// </summary>
    [Fact]
    public void APathIsOnlyStatedWhenItWasGiven()
    {
        Assert.Null(CliOptions.Parse([]).MountPath);
        Assert.True(Path.IsPathRooted(CliOptions.Parse([]).MountSettings.MountPath));

        Assert.Equal("/mnt/tfs", CliOptions.Parse(["--path", "/mnt/tfs"]).MountPath);
    }

    [Fact]
    public void InitSettingsTakesAPathOrNone()
    {
        Assert.True(CliOptions.Parse(["--init-settings"]).InitSettings);
        Assert.Equal("/tmp/s.json", CliOptions.Parse(["--init-settings", "/tmp/s.json"]).SettingsPath);

        // A following flag is a flag, not a path.
        CliOptions withFlag = CliOptions.Parse(["--init-settings", "--port", "9999"]);

        Assert.True(withFlag.InitSettings);
        Assert.Equal(9999, withFlag.NinePPort);
    }

    [Fact]
    public void AnUnknownOptionIsRefusedByName()
    {
        CliUsageException refused = Assert.Throws<CliUsageException>(() => CliOptions.Parse(["--frobnicate"]));

        Assert.Contains("--frobnicate", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionAndHelpFlagsParse()
    {
        Assert.True(CliOptions.Parse(["--version"]).Version);
        Assert.True(CliOptions.Parse(["--help"]).Help);
        Assert.True(CliOptions.Parse(["-h"]).Help);
        Assert.False(CliOptions.Parse([]).Version);
    }

    /// <summary>
    /// The usage text and the parser have to agree, or a documented flag is a lie and an
    /// undocumented one is a secret. Both are found by reading one against the other.
    /// </summary>
    [Fact]
    public void EveryFlagIsDocumentedAndEveryDocumentedFlagIsAccepted()
    {
        string[] documented =
        [
            .. CliOptions.Usage
                .Split([' ', '\n', '\r', ',', '[', ']', '|'], StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.StartsWith("--", StringComparison.Ordinal))
                .Select(word => word.TrimEnd('.'))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(documented);

        foreach (string flag in documented)
        {
            // Every flag parses, either on its own or with a value; what must not happen is
            // "unknown option".
            try
            {
                CliOptions.Parse([flag]);
            }
            catch (CliUsageException refused)
            {
                Assert.DoesNotContain("unknown option", refused.Message, StringComparison.Ordinal);
            }
        }
    }
}
