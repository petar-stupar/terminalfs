namespace TerminalFs.Core;

/// <summary>
/// The names a caller may give a command. An id becomes a directory name, so it is bounded by
/// what a directory name may be before it is bounded by anything this program wants.
/// </summary>
/// <remarks>
/// The alphabet is deliberately smaller than what 9P would carry. A name reaching a handler has
/// already been refused if it is <c>.</c>, <c>..</c>, contains a slash or a NUL, or runs past 255
/// bytes — but a name that merely makes a shell awkward, or that a caller cannot type back into
/// a path without quoting, is still a bad name for a directory an agent has to <c>cd</c> into.
/// A leading dot is refused because a hidden command directory is one an <c>ls</c> does not show
/// and a caller then cannot find.
/// </remarks>
public static class CommandId
{
    /// <summary>The longest an id may be.</summary>
    public const int MaxLength = 64;

    /// <summary>Whether <paramref name="id"/> may name a command.</summary>
    public static bool IsValid(string? id)
    {
        if (id is null || id.Length == 0 || id.Length > MaxLength || id[0] == '.')
        {
            return false;
        }

        foreach (char character in id)
        {
            bool allowed = character
                is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '_' or '.' or '-';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns <paramref name="id"/>, or refuses it with the reason.</summary>
    /// <exception cref="CommandException">The id is not one this tree can carry.</exception>
    public static string Require(string? id)
    {
        if (IsValid(id))
        {
            return id!;
        }

        throw new CommandException(
            $"'{id}' cannot name a command; use 1 to {MaxLength} of letters, digits, '_', '-' or "
                + "'.', not starting with '.'",
            CommandErrno.InvalidArgument);
    }
}
