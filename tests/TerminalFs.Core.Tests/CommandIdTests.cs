namespace TerminalFs.Core.Tests;

/// <summary>
/// What may name a command. The id becomes a directory an agent has to walk into and write in a
/// path, so the alphabet is smaller than what the protocol would carry.
/// </summary>
public sealed class CommandIdTests
{
    [Theory]
    [InlineData("t1")]
    [InlineData("toolu_01DePTGJwEBHNpDusQsBiGBn")]
    [InlineData("build-step.2")]
    [InlineData("A")]
    [InlineData("0")]
    public void AnIdOfLettersDigitsDotDashUnderscoreIsAccepted(string id) =>
        Assert.Equal(id, CommandId.Require(id));

    [Fact]
    public void AnIdStartingWithADotIsRefused() =>
        Assert.False(CommandId.IsValid(".hidden"));

    [Fact]
    public void AnEmptyIdIsRefused() => Assert.False(CommandId.IsValid(""));

    [Fact]
    public void AnIdLongerThanSixtyFourIsRefused()
    {
        Assert.True(CommandId.IsValid(new string('a', CommandId.MaxLength)));
        Assert.False(CommandId.IsValid(new string('a', CommandId.MaxLength + 1)));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a\tb")]
    [InlineData("a*b")]
    [InlineData("a\nb")]
    [InlineData("..")]
    public void AnIdWithASlashOrSpaceIsRefused(string id) => Assert.False(CommandId.IsValid(id));

    /// <summary>
    /// The refusal has to name the id and say what was expected: it reaches the caller as the
    /// error on a write, which is the only place they will see it.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheIdAndWhatWasExpected()
    {
        CommandException refused = Assert.Throws<CommandException>(() => CommandId.Require("a b"));

        Assert.Contains("a b", refused.Message, StringComparison.Ordinal);
        Assert.Contains("letters, digits", refused.Message, StringComparison.Ordinal);
        Assert.Equal(CommandErrno.InvalidArgument, refused.Errno);
    }
}
