namespace TerminalFs.Tests;

/// <summary>
/// What <c>--version</c> answers. The flag exists so that a bug report can name both versions,
/// which only helps if every line really carries one: a missing attribute would otherwise report
/// an empty version as confidently as a real one.
/// </summary>
public class VersionsTests
{
    [Fact]
    public void EveryComponentIsNamedWithARealVersion()
    {
        string[] lines = Versions.Report.Split(Environment.NewLine);

        Assert.Equal(["terminalfs", "NineP.Server", "NineP.Protocol"], lines.Select(line => line.Split(' ')[0]));

        foreach (string line in lines)
        {
            string version = line.Split(' ')[1];

            Assert.True(
                Version.TryParse(version.Split('-')[0], out _),
                $"'{line}' does not carry a version");
        }
    }

    /// <summary>
    /// The build metadata a source-linked build appends is not part of the answer, and leaving it
    /// on would make the line disagree with the package version it is being compared against.
    /// </summary>
    [Fact]
    public void NoLineCarriesBuildMetadata() =>
        Assert.DoesNotContain("+", Versions.Report, StringComparison.Ordinal);
}
