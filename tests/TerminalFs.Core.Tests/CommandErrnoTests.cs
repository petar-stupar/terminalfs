namespace TerminalFs.Core.Tests;

/// <summary>
/// The numbers are POSIX's, and a client reads them as POSIX's. Restating them in this library
/// is only safe while they still say what POSIX says.
/// </summary>
public sealed class CommandErrnoTests
{
    [Fact]
    public void TheNumbersAreThePosixOnesAClientWillReadThemAs()
    {
        Assert.Equal(22, CommandErrno.InvalidArgument);
        Assert.Equal(2, CommandErrno.NotFound);
        Assert.Equal(17, CommandErrno.Exists);
        Assert.Equal(1, CommandErrno.NotPermitted);
        Assert.Equal(27, CommandErrno.TooLarge);
        Assert.Equal(39, CommandErrno.NotEmpty);
        Assert.Equal(30, CommandErrno.ReadOnly);
        Assert.Equal(21, CommandErrno.IsDirectory);
        Assert.Equal(20, CommandErrno.NotDirectory);
    }
}
