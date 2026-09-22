using TerminalFs.Core.Permissions;

namespace TerminalFs.Core;

/// <summary>What the registry was told to do, settled before anything runs.</summary>
public sealed record CommandOptions
{
    /// <summary>The shell, or null to take the caller's own.</summary>
    public string? Shell { get; init; }

    /// <summary>The directory commands run in, or null for this process's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// How long a finished command is kept after the last handle on it closes.
    /// </summary>
    public TimeSpan KeepAfterExit { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a read of <c>wait</c> blocks before answering <c>running</c>.
    /// </summary>
    /// <remarks>
    /// Shorter than any client's own timeout, because a read that outlives the client's patience
    /// is reported as a broken mount rather than as a command still working. Twenty-five seconds
    /// clears SMB's usual sixty with room, and a caller who wants longer reads it again.
    /// </remarks>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>The most one write to the control file may accumulate before it is refused.</summary>
    public int MaxControlBytes { get; init; } = 1024 * 1024;

    /// <summary>Where the output files live, or null for a directory under the temporary one.</summary>
    public string? OutputRoot { get; init; }

    /// <summary>
    /// Where this tree can be read from, or null when nobody has said.
    /// </summary>
    /// <remarks>
    /// A caption and nothing more: the served skill prints it in place of a placeholder, because
    /// that page is the one meant to be copied out of the tree and followed from outside it.
    /// Nothing in here acts on it, and null rather than a default because a path nobody stated is
    /// a guess, and a skill naming a directory that is not there is worse than one that asks to
    /// be filled in.
    /// </remarks>
    public string? MountPath { get; init; }

    /// <summary>The rules in force, or null to refuse nothing.</summary>
    /// <remarks>
    /// Null is for the suite, which mostly tests something other than the rules. The program
    /// itself never reaches here without a settings file: see <see cref="SettingsWatcher"/>.
    /// </remarks>
    public SettingsWatcher? Settings { get; init; }

    /// <summary>The clock the removal timer runs on.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The rules in force right now, which is none when there is no settings file.</summary>
    public DenyList Deny => Settings?.Current ?? DenyList.Empty;
}
