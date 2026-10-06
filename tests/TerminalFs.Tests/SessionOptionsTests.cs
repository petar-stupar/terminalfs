using TerminalFs.Internal.Sessions;

namespace TerminalFs.Tests;

/// <summary>
/// The <c>terminalfs session</c> command line. The id becomes a directory name and a hook passes
/// it in, so what is accepted is pinned rather than left to whatever the parser happens to do.
/// </summary>
public sealed class SessionOptionsTests
{
    [Theory]
    [InlineData("start", nameof(SessionAction.Start))]
    [InlineData("stop", nameof(SessionAction.Stop))]
    [InlineData("serve", nameof(SessionAction.Serve))]
    public void EachActionOnASessionNamesIt(string action, string expected)
    {
        SessionOptions options = SessionOptions.Parse([action, "--id", "abc"]);

        Assert.Equal(expected, options.Action.ToString());
        Assert.Equal("abc", options.Id);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("serve")]
    public void AnActionOnASessionNeedsItsId(string action)
    {
        CliUsageException refused = Assert.Throws<CliUsageException>(() => SessionOptions.Parse([action]));

        Assert.Contains("--id", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdThatIsNotADirectoryNameIsRefused() =>
        Assert.Throws<CliUsageException>(() => SessionOptions.Parse(["start", "--id", "../../etc"]));

    [Fact]
    public void CollectingTakesNoId() =>
        Assert.Throws<CliUsageException>(() => SessionOptions.Parse(["gc", "--id", "abc"]));

    [Fact]
    public void AnUnknownActionIsRefusedByName()
    {
        CliUsageException refused = Assert.Throws<CliUsageException>(() => SessionOptions.Parse(["restart"]));

        Assert.Contains("restart", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoActionIsRefused() =>
        Assert.Throws<CliUsageException>(() => SessionOptions.Parse([]));

    [Fact]
    public void TheWorkingDirectoryIsPassedThrough() =>
        Assert.Equal("/src/app", SessionOptions.Parse(["start", "--id", "a", "--cwd=/src/app"]).WorkingDirectory);

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("12h", 43200)]
    [InlineData("7d", 604800)]
    [InlineData("60", 60)]
    public void AnAgeIsANumberWithAUnit(string text, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SessionOptions.Parse(["gc", "--older-than", text]).OlderThan);

    [Theory]
    [InlineData("soon")]
    [InlineData("h")]
    [InlineData("-5m")]
    [InlineData("99999999d")]
    public void SomethingThatIsNotAnAgeIsRefused(string text) =>
        Assert.Throws<CliUsageException>(() => SessionOptions.Parse(["gc", "--older-than", text]));

    [Fact]
    public void CollectingStopsOnlyTheDeadUnlessGivenAnAge() =>
        Assert.Null(SessionOptions.Parse(["gc"]).OlderThan);

    /// <summary>A hook that passes a flag believes it did something, so one that would not is refused.</summary>
    [Theory]
    [InlineData("start", "--older-than", "5m")]
    [InlineData("stop", "--older-than", "5m")]
    [InlineData("stop", "--cwd", "/tmp")]
    [InlineData("gc", "--cwd", "/tmp")]
    [InlineData("start", "--owner", "1234:1700000000000")]
    [InlineData("gc", "--owner", "1234:1700000000000")]
    public void AFlagThatDoesNothingForTheActionIsRefused(string action, string flag, string value)
    {
        string[] args = action == "gc" ? [action, flag, value] : [action, "--id", "a", flag, value];

        CliUsageException refused = Assert.Throws<CliUsageException>(() => SessionOptions.Parse(args));

        Assert.Contains(flag, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AStopCanBeLimitedToTheProcessThatOwnsTheSession()
    {
        SessionOwner? owner = SessionOptions.Parse(["stop", "--id", "a", "--owner", "1234:1700000000000"]).Owner;

        Assert.Equal(new SessionOwner(1234, DateTimeOffset.FromUnixTimeMilliseconds(1700000000000)), owner);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("1:1700000000000")]
    [InlineData("1234:")]
    [InlineData("-5:1700000000000")]
    [InlineData("1234:soon")]
    public void AnOwnerThatIsNotAProcessIsRefused(string text) =>
        Assert.Throws<CliUsageException>(() => SessionOptions.Parse(["stop", "--id", "a", "--owner", text]));

    [Fact]
    public void TheMainCommandLineHandsSessionOverRatherThanRefusingIt() =>
        Assert.Contains("session", CliOptions.Usage, StringComparison.Ordinal);

    /// <summary>
    /// Port 0 asks the system for a free port, and a mount that named 0 — or the default port,
    /// where another server may well be listening — would attach to the wrong tree or none.
    /// </summary>
    [Fact]
    public async Task AServerOnPortZeroMountsThePortItWasGiven()
    {
        await using Served served = await Served.StartAsync();

        int? port = Program.BoundPort([served.Address]);

        Assert.NotNull(port);
        Assert.NotEqual(0, port);
        Assert.Contains($":{port}", served.Address.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The usage text and the parser have to agree, as they do for the main command.</summary>
    [Fact]
    public void EverySessionFlagIsDocumentedAndEveryDocumentedFlagIsAccepted()
    {
        string[] documented =
        [
            .. SessionOptions.Usage
                .Split([' ', '\n', '\r', ',', '[', ']', '|'], StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.StartsWith("--", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.Equal(["--id", "--cwd", "--owner", "--older-than", "--help"], documented);

        foreach (string flag in documented)
        {
            try
            {
                SessionOptions.Parse(["gc", flag]);
            }
            catch (CliUsageException refused)
            {
                Assert.DoesNotContain("unknown option", refused.Message, StringComparison.Ordinal);
            }
        }
    }
}
