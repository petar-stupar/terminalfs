# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

The `## [<version>]` section of a release is its release notes, verbatim; the release workflow
refuses a tag whose version has no section here.

## [Unreleased]

## [0.1.0] — 2026-09-16

### Added

- **Shell commands as a filesystem.** `echo '<command>' > /ctl/<name>` runs it, and
  `/cmd/<name>/` holds what it did: `command`, `status`, `stdout`, `stderr` and `wait`, plus
  `pid` and `kill` while it runs and `exitcode` once it has stopped.
- **`wait` blocks** until a command stops, then prints its state — an alternative to polling
  `status`. It gives up after `--wait-timeout` seconds (25 by default) and prints `running`,
  because a read that outlives a client's own patience is reported as a broken mount rather than
  as a command still working.
- **Output is a file that grows.** A read ends at what has arrived rather than waiting for more,
  so `cat`, `tail -n`, `grep` and `wc -l` all end. Waiting is what `wait` is for, and it is a
  separate file so the choice is the caller's.
- **A deny list, in Claude Code's spelling**, so a list written for an agent harness can be copied
  across whole. Rules are applied to the command as a whole and to each of its segments, because
  `cd /tmp && sudo ls` is two commands. `--init-settings` writes one with sane defaults.
- **The settings file is re-read while the server runs**, and an edit that does not parse keeps
  the rules already in force: an editor that truncates before it writes leaves a window in which
  the file is empty, and empty for a deny list means everything is permitted. A missing or
  malformed file at startup stops the server, because running commands under rules nobody wrote is
  worse than not starting.
- **A finished command is removed on its own** once nothing has read it for `--keep` seconds (60
  by default). Removing one that is still running kills it.

### Notes

- **A file per command, not one control file.** A shared `/ctl` cannot carry concurrent writes
  through a mount, and not because of anything this server does: four callers writing at once
  reach it as **one** write, because macOS smbfs merges writes to a path in its page cache. Three
  commands were lost with no error anywhere — the worst outcome available, since a caller is told
  their command ran. The name is the path instead, and eight simultaneous writers produce eight
  commands.
- **A control file reports no length.** When `/ctl` answered with help text, a client read it, laid
  the command over the front and sent back the whole thing, so what ran was the command followed by
  the tail of its own help and the shell reported a parse error in a line nobody wrote. A file with
  no length has nothing to merge into. The consequence is that reading one through an SMB mount
  returns nothing, which is what `/ctl/index.md` and `/refused` are for.
- **Refusals are readable at `/refused`.** A refusal reaches a mounted caller as a number and
  nothing else — 9P2000.L carries no sentence with an error — so the reason has to be somewhere
  they can go and read it.
- **`--listen` off loopback is refused, with no flag to override it.** This server runs whatever is
  written to a control file, as the user who started it; whoever can open the socket gets a shell.

[Unreleased]: https://github.com/petar-stupar/terminalfs/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/petar-stupar/terminalfs/releases/tag/v0.1.0
