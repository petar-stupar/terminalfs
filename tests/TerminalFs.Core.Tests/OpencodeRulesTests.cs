using TerminalFs.Core.Permissions;

namespace TerminalFs.Core.Tests;

/// <summary>
/// opencode's shell rules, applied to a command run through the tree the way opencode applies
/// them to the same command run with its shell tool. Where they disagree, a rule somebody wrote
/// means one thing at opencode and another here.
/// </summary>
public sealed class OpencodeRulesTests
{
    private static OpencodeRule Rule(string resource, string effect, string action = "shell") => new(action, resource, effect);

    /// <summary>Order decides, not specificity: a project's rules follow the global ones and win.</summary>
    [Fact]
    public void TheLastMatchingRuleWins()
    {
        OpencodeRule[] rules = [Rule("git push *", "deny"), Rule("git *", "allow")];

        Assert.Equal("allow", OpencodeRules.Decide("git push origin main", rules).Effect);
        Assert.Equal("deny", OpencodeRules.Decide("git push origin main", [.. rules.Reverse()]).Effect);
    }

    [Fact]
    public void ACommandNoRuleMatchesIsAskedAbout() =>
        Assert.Equal("ask", OpencodeRules.Decide("make", [Rule("git *", "allow")]).Effect);

    [Fact]
    public void ARuleForEveryActionCoversTheShell() =>
        Assert.Equal("allow", OpencodeRules.Decide("make", [Rule("*", "allow", action: "*")]).Effect);

    [Fact]
    public void ATrailingSpaceStarAlsoMatchesTheBareCommand()
    {
        OpencodeRule[] rules = [Rule("ls *", "allow")];

        Assert.Equal("allow", OpencodeRules.Decide("ls", rules).Effect);
        Assert.Equal("allow", OpencodeRules.Decide("ls -la /tmp", rules).Effect);
        Assert.Equal("ask", OpencodeRules.Decide("lsof", rules).Effect);
    }

    [Theory]
    [InlineData("ls && sudo reboot")]
    [InlineData("echo $(sudo id)")]
    [InlineData("(sudo ls)")]
    public void ADeniedCommandAnywhereInTheLineRefusesIt(string command) =>
        Assert.Equal("deny", OpencodeRules.Decide(command, [Rule("*", "allow"), Rule("sudo *", "deny")]).Effect);

    [Fact]
    public void EveryCommandInTheLineHasToBeAllowed()
    {
        OpencodeRule[] rules = [Rule("ls *", "allow")];

        Assert.Equal("ask", OpencodeRules.Decide("ls; make", rules).Effect);
        Assert.Equal("allow", OpencodeRules.Decide("ls; ls -la", rules).Effect);
    }

    /// <summary>opencode checks where a cd goes, not the cd as a command.</summary>
    [Fact]
    public void ChangingDirectoryIsNotACommandToCheck() =>
        Assert.Equal("allow", OpencodeRules.Decide("cd src && ls", [Rule("ls *", "allow")]).Effect);

    [Fact]
    public void TheWildcardIsAnchoredAndCrossesSpacesAndSlashes()
    {
        Assert.True(OpencodeRules.Matches("rm -rf /tmp/x", "rm *"));
        Assert.True(OpencodeRules.Matches("cat a.txt", "cat ?.txt"));
        Assert.False(OpencodeRules.Matches("echo rm -rf", "rm *"));
        Assert.False(OpencodeRules.Matches("gitx", "git"));
    }
}
