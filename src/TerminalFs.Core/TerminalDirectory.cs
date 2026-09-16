namespace TerminalFs.Core;

/// <summary>A directory of the served tree.</summary>
public abstract class TerminalDirectory : TerminalNode
{
    private protected TerminalDirectory(string name, TerminalNodeKind kind, string key)
        : base(name, kind, key)
    {
    }

    /// <summary>The entries of this directory, in the order they should be listed.</summary>
    public abstract IReadOnlyList<TerminalNode> Children { get; }

    /// <summary>
    /// Whether a caller may change what is in here. Only the command directories are: a client
    /// cannot remove <c>/cmd</c> itself, nor anything under <c>/skills</c>.
    /// </summary>
    public virtual bool Writable => false;

    /// <summary>
    /// The child named <paramref name="name"/>, or null. Names are matched exactly: 9P is
    /// case-sensitive, and a tree that guessed would hand a caller a different command than the
    /// one they walked to.
    /// </summary>
    public virtual TerminalNode? Find(string name)
    {
        foreach (TerminalNode child in Children)
        {
            if (string.Equals(child.Name, name, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>Removes <paramref name="name"/> from this directory.</summary>
    /// <param name="name">What to remove.</param>
    /// <param name="directory">Whether the caller believes it is a directory.</param>
    /// <exception cref="CommandException">It is not there, or this directory does not change.</exception>
    public virtual void Remove(string name, bool directory) =>
        throw new CommandException("nothing here can be removed", CommandErrno.ReadOnly);
}
