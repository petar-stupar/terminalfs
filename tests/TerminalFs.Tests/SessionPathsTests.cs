using TerminalFs.Internal.Sessions;

namespace TerminalFs.Tests;

/// <summary>
/// Where sessions live and what may name one. A session id comes from an agent's hook and becomes
/// a directory that is mounted over as root, so what is accepted is pinned here.
/// </summary>
public sealed class SessionPathsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("has space")]
    public void AnIdThatCouldLeaveTheRuntimeDirectoryIsRefused(string id) =>
        Assert.False(SessionPaths.IsValidId(id));

    /// <summary>
    /// A session's record and log sit beside its directory, so an id spelled like one of them
    /// would make one session's directory another's record.
    /// </summary>
    [Theory]
    [InlineData("abc.session")]
    [InlineData("abc.log")]
    [InlineData("abc.owner")]
    [InlineData("abc.session.tmp")]
    [InlineData("abc.LOG")]
    public void AnIdSpelledLikeASessionsOwnFilesIsRefused(string id) =>
        Assert.False(SessionPaths.IsValidId(id));

    [Fact]
    public void AnIdLongerThanTheLimitIsRefused()
    {
        Assert.True(SessionPaths.IsValidId(new string('a', SessionPaths.MaxIdLength)));
        Assert.False(SessionPaths.IsValidId(new string('a', SessionPaths.MaxIdLength + 1)));
    }

    [Theory]
    [InlineData("0b5c3f5e-6a8f-4a55-9c1e-2d6c1c1c9f10")]
    [InlineData("ses_2a9f.v1")]
    public void TheIdsAgentsUseAreAccepted(string id) =>
        Assert.True(SessionPaths.IsValidId(id));

    [Fact]
    public void SessionsLiveInTheRuntimeDirectory()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "run-user");

        Assert.Equal(
            Path.Combine(runtime, "terminalfs"),
            SessionPaths.Resolve(Environment(("XDG_RUNTIME_DIR", runtime)), "home").Root);
    }

    /// <summary>
    /// For a machine that wants every agent's trees in a directory it chose, without moving what
    /// else reads <c>$XDG_RUNTIME_DIR</c>.
    /// </summary>
    [Fact]
    public void TerminalfsOwnRuntimeDirectoryComesFirst()
    {
        string runtime = Path.Combine(Path.GetTempPath(), "run-user");
        string chosen = Path.Combine(Path.GetTempPath(), "sessions");

        Assert.Equal(
            Path.Combine(chosen, "terminalfs"),
            SessionPaths.Resolve(
                Environment(("XDG_RUNTIME_DIR", runtime), ("TERMINALFS_RUNTIME_DIR", chosen)),
                "home").Root);

        Assert.Equal(
            Path.Combine(runtime, "terminalfs"),
            SessionPaths.Resolve(
                Environment(("XDG_RUNTIME_DIR", runtime), ("TERMINALFS_RUNTIME_DIR", "relative")),
                "home").Root);
    }

    [Fact]
    public void WithoutARuntimeDirectorySessionsLiveInTheCache()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");
        string cache = Path.Combine(Path.GetTempPath(), "cache");

        Assert.Equal(Path.Combine(home, ".cache", "terminalfs"), SessionPaths.Resolve(Environment(), home).Root);
        Assert.Equal(
            Path.Combine(cache, "terminalfs"),
            SessionPaths.Resolve(Environment(("XDG_CACHE_HOME", cache)), home).Root);
    }

    /// <summary>The XDG specification says a relative path in these variables is to be ignored.</summary>
    [Fact]
    public void ARelativeRuntimeDirectoryIsIgnored()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");

        Assert.Equal(
            Path.Combine(home, ".cache", "terminalfs"),
            SessionPaths.Resolve(Environment(("XDG_RUNTIME_DIR", "relative")), home).Root);
    }

    [Fact]
    public void TheLockIsNeverASession() =>
        Assert.False(SessionPaths.IsValidId(Path.GetFileName(new SessionPaths("/r").LockPath)));

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(variable => variable.Name == name).Value;
}
