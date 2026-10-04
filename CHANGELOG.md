# Changelog

Notable changes, newest first. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the versions follow [semantic versioning](https://semver.org/spec/v2.0.0.html).

The `## [<version>]` section of a release is its release notes, verbatim; the release workflow
refuses a tag whose version has no section here.

## [Unreleased]

### Added

- **Strict mode for the Claude Code hook.** With `TERMINALFS_CLAUDE_STRICT` set to `1` or `true`
  in the hook's environment, a Bash call that bypasses the session's tree, or reads the tree
  alongside other commands, is refused instead of left to Claude Code, with a reason that names
  the tree and the shape to run the command in. Claude Code's own rules cannot express "Bash only
  through the tree", because a deny on `Bash` also refuses the call that writes to `ctl/`. It
  applies in every permission mode but `plan`, `auto` included, and only to the Bash tool; without
  the variable nothing changes.

## [0.4.0] — 2026-10-01

### Added

- **`terminalfs session start|stop|gc`: a tree per agent session.** One shared server cannot tell
  which session wrote to `/ctl`; a session of its own makes that a question about the path. `start
  --id <id> [--cwd <dir>]` runs a server on a free loopback port in the background, mounts it at
  `<runtime-dir>/terminalfs/<id>` and prints that path, and is idempotent for the same id. `stop
  --id <id>` stops the server, which kills its commands, unmounts, and removes the directory, and
  is safe when nothing is there. `gc` does the same for sessions whose server is gone, including
  the commands a killed server left running: the ones that still carry its token,
  `TERMINALFS_SESSION_TOKEN`, in their environment, so a command started with a cleared
  environment is left alone. With `--older-than <duration>` it also stops live sessions that
  old. Linux only for now: the macOS bridge serves one tree at a time. Sessions need the
  settings file, `setsid`, and root or `sudo` for `mount` and `umount`.
- **A Claude Code plugin: a tree per session, held to the session's own permission rules.**
  `claude plugin marketplace add petar-stupar/terminalfs` and `claude plugin install
  terminalfs@terminalfs`. Its hooks run `terminalfs hook claude session-start`, `session-end` and
  `pre-tool-use`: the tree is mounted when a session starts and the agent told where, and a stop
  is started when the session ends that outlives Claude Code's second and a half for those hooks. Claude Code checks its rules against the Bash call that writes a
  command into the tree, not the command, so the hook reads the command back out and checks it
  against the same `Bash(...)` rules from the managed, user, project and local settings files —
  deny, then ask, then allow, on the whole command and each subcommand — and what no rule decides
  follows the permission mode: `default` and `acceptEdits` ask, `bypassPermissions` runs,
  `dontAsk` refuses, `auto` leaves it to auto mode's classifier, and `plan` refuses everything. A
  write into a tree in any shape it cannot read a command out of, into another session's tree, or
  through the variables the trees' directory came from is refused. Only rules in settings files
  are read: command-line flags, a skill's `allowed-tools`, approvals for the session and managed
  policy that is not a file are Claude Code's alone. It is a check an agent following its instructions stays inside,
  not a boundary: the agent runs as the same user as the server.
- **An opencode plugin, in `plugins/opencode`, for opencode v2.** It gives each session a tree of
  its own, started before the first prompt and stopped when the session is deleted or opencode's
  server stops, with its path in the session's context and the opencode skill added to it. A
  command written with the `write` tool to `ctl/<name>` is checked, inside opencode's own
  permission check for that write, against the session's `shell` rules (and `permission.bash`,
  which opencode migrates) as opencode would read them for its shell tool: a deny refuses the write
  naming the rule, an ask shows opencode's prompt with the command as the diff, and an allow runs
  it. The built-in `read` and `write` are made callable from `execute` as `file_read` and
  `file_write` (the plugin's `codemode` option names more), so a script writes commands and reads
  what they did in one turn; a write made on its own gets the command's state, exit code and
  the last 64 KiB of its output added to its result. A write is judged on where opencode resolved its path to, however it was spelled.
  Writes into another session's tree, edits and patches in a tree, and shell calls that write into
  one in any shape but the skill's are refused, and a `cd` out of the project in a command is
  asked about as opencode's shell tool would ask. An "always" answer to the prompt is opencode's
  for an edit, and allows every edit in the project. `terminalfs hook opencode
  session-start|session-end|check|skill` is what it runs: the skill comes from the binary, so
  the plugin is `index.js` alone.
- **`$TERMINALFS_RUNTIME_DIR`** says where session trees live, before `$XDG_RUNTIME_DIR`.
- **`terminalfs plugin install claude|opencode [--dir <dir>]`: the plugins come with the binary.**
  A plugin is what starts a session's tree, so it cannot be served from one; the binary carries
  the plugin written for it instead. `opencode` writes the plugin to
  `$XDG_CONFIG_HOME/opencode/plugins/terminalfs/`, where opencode finds it. `claude` writes a
  marketplace laid out as the repository's to `$XDG_DATA_HOME/terminalfs/claude-code` and prints
  the `claude plugin` commands that add it, since Claude Code installs plugins only from a
  marketplace. The Claude Code skill in it is rendered from the program rather than copied.
  Run it again after an upgrade.

### Changed

- **The opencode skill's `execute` script uses opencode v2's own read and write.** v2's code mode
  leaves its built-in file tools out; the plugin copies `read` and `write` into it as `file_read`
  and `file_write`, sharing the built-ins' permission checks, and the script reads what they
  return as `.content`. Without `execute`, one `write` whose result the plugin fills in with what
  the command did. Where the skill does not name the tree, it says the session context does.
- **Reading back a command that has been written, before it runs, gives the command.** A write tool
  that checks what it wrote — opencode's does, at once — got `EEXIST` and reported a write that
  failed, for a command that was about to run. Writing to the name again is still refused.
- **A skill per harness: `/skills/opencode/terminalfs/SKILL.md` and
  `/skills/claude-code/terminalfs/SKILL.md`.** The cheapest way to run a command depends on the
  tools a harness has, and one skill describing every way was longer and followed worse. opencode's
  writes the command and reads `wait` and `stdout` in one `execute` script; Claude Code's does it in
  one Bash call of a fixed shape — `cat > <mount>/ctl/<name> <<'CMD'`, the command, `CMD`, then `cat`
  of `wait` and `stdout` — which a permission check can read the command out of. Both say to read
  `wait` before `stdout`, and not to spend a call removing command directories. Point opencode's
  `skills.paths` at `skills/opencode` under the mountpoint; copy the Claude Code one into
  `$CLAUDE_CONFIG_DIR/skills/terminalfs/`, replacing `<mount>` if it is still there. When the
  mountpoint is not known, each says where to find it, and the Claude Code one gives way to a mount
  the session context names.

### Removed

- **`/skills/terminalfs/` is gone.** Anything copying `/skills/terminalfs/SKILL.md` needs to copy
  the skill for its harness instead.

### Fixed

- **`Bash(ls *)` in the deny list matches `ls` as well as `ls -la`**, and still not `lsof`: a space
  and a trailing `*` mean what `:*` means when the `*` is the rule's only one, which is how Claude
  Code reads the same rule.
- **`--listen tcp://127.0.0.1:0 --mount` mounts the port the server was given.** It used to mount
  the default port instead, where it found another server's tree or none.
- **`--unmount` reads a mount's port whole.** A server on port 4000 recognised a mount of port
  40001 as its own.
- **A mount or unmount that times out says so.** Killing one that ran long could fail on the part
  of it that belongs to root, the mount under a `sudo`, and the program ended with a stack trace.
- **Command output is readable by its user alone.** It was written under `/tmp/terminalfs/<pid>`
  with default modes, so every user on the machine could read what a command printed, and the
  first user's `/tmp/terminalfs` kept anyone else's server from starting. It now goes under
  `$XDG_CACHE_HOME/terminalfs-output` (`~/.cache/terminalfs-output`), in directories only the
  user can open; Windows keeps its temporary directory, which is the user's own. What servers
  that are gone left in `/tmp/terminalfs` is cleared the next time one starts. Where the home
  directory cannot be written, set `XDG_CACHE_HOME` to somewhere that can.
- **`--keep 0` no longer breaks a write tool that creates its file first.** A name nobody has
  written to yet is held for at least ten seconds, so the write after an empty create finds it.
- **A command whose output cannot be made no longer takes the server down.** A full disk or no
  file handles left, met while a written name was starting, ended the process from a timer, or left
  the name stuck in `/ctl` for good. The name is given back and the reason logged.

## [0.3.2] — 2026-09-22

### Changed

- **A name under `/ctl` is only taken until something decides it, and `/cmd/<name>/` does not
  exist before that.** Opening a control file used to make the command's directory on the spot, so
  every probe open, every temporary file and every read of `/ctl/<name>` left a directory behind
  for a command that never ran. A name you take and never write to now leaves nothing, is listed
  under `/ctl` while it is in flight, and is freed by `--keep` like a finished command. `rm
  /ctl/<name>` gives one back by hand.
- **Write the control file however your tools write files.** Creating it before writing to it
  works, and so does writing to a temporary name and renaming it into place — which is what an
  agent harness's write tool does. The command runs under the name you renamed it to, never under
  the temporary one.
- **A close carrying bytes no longer spawns; it decides the name, which then settles.** A client
  that writes atomically closes its temporary file *before* it renames, so the close is the only
  signal there is and running on it would run the command under a name nobody chose. `--settle`
  says how long that window is, 250 milliseconds by default, and `--settle 0` runs at the close as
  before. Nothing waits it out in practice: anything that asks about the command under `/cmd` runs
  it at once, so `echo … > ctl/t1; cat cmd/t1/wait` is unchanged.
- **A name that has been written to reports how long its command is.** A control file still
  reports no length while it can be written — that zero is what stops a client merging its own
  cache into the command it is about to send — but once the name has been decided nothing can open
  it again, and a client that writes a file atomically and then stats it to check what it wrote
  gets an answer rather than a zero it reports as a silent failure.
- A copy of `SKILL.md` taken before this release does not know it can create a file before writing
  to it, or rename one into place. Take it again.

### Fixed

- **An exclusive create of a control file always failed.** Every syntactically valid name resolved
  on a walk, so the core never reached the create and `O_CREAT|O_EXCL` could only ever answer
  `EEXIST` — which is how a client that writes to a temporary file first was stopped before it
  started. A name nobody has taken is now no file, and creating it is what takes it.
- **A reused name could inherit the removed command's qid.** `rm -r /cmd/build` followed by a new
  `build` handed the new command the old one's identity, and a client caching on it would serve
  the removed command's output for the new one. A command is identified by an ordinal now, not by
  its name.

- **The served `SKILL.md` names the mountpoint** when this server was told one — because it did
  the mounting, or because `--path` said where you would mount it yourself. Otherwise it keeps
  writing `<mount>` for you to replace: a path nobody stated would be a guess, and a skill naming
  a directory that is not there is worse than one that asks to be filled in. `--path` now means
  something without `--mount` for exactly this. If the agent reading the skill is in a different
  filesystem namespace from the server — a container that bind-mounts the host's mountpoint
  elsewhere — the path is still the server's, and there is no flag for that yet.
- `/cmd/index.md` told a reader with no commands yet to run `echo 'run first echo hello' > /ctl`,
  which is the single-control-file protocol removed in 0.2.0. It is the one page an agent reads
  before it has run anything.

## [0.2.0] — 2026-09-18

### Changed

- **A command refused before it ran keeps its name and gets its directory.** `/cmd/<name>/` holds
  `command`, `status` — which reads `denied` — and `reason`, and nothing else: no `pid`, no
  `exitcode`, no `stdout`, no `stderr`, no `wait` and no `kill`, because there was no process for
  any of them to describe. The write to `/ctl/<name>` still fails, which is where a caller sees
  that something went wrong; the directory is where they read what.
- **Every pre-run failure ends the same way.** A rule caught at the write and a rule caught at the
  close now produce the same thing, as do a command past `--max-bytes` and a control file closed
  with nothing in it. The close-time refusal used to be silent — an `error` with `exitcode -1` and
  the reason on `stderr` — so the same refusal looked like two different events depending on which
  write it arrived on.
- **A refused command is cleared up by the same timer as a finished one**, `--keep` seconds after
  the last read, and `rm -r /cmd/<name>` removes it by hand. There is no second mechanism. Because
  the name is now spent, removing the directory is also what frees it.

### Removed

- **`/refused` is gone.** A queue of the last eight refusals was the wrong shape for the question
  being asked: a caller wants the reason for *their* write, and a shared log made them pick it out
  of other people's by timestamp, could evict it before they looked, and only ever held eight. The
  reason now belongs to the command it is about. The one refusal with nowhere to go is a name
  already taken — there is no command of ours to write it on — and there `ls /cmd/<name>` is the
  answer, since the directory existing is why the name was not free.
- The served `SKILL.md` changed with it. A copy taken from `/skills/terminalfs/SKILL.md` before
  this release tells an agent to read `/refused`, which no longer exists; take it again.

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

[Unreleased]: https://github.com/petar-stupar/terminalfs/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/petar-stupar/terminalfs/compare/v0.3.2...v0.4.0
[0.3.2]: https://github.com/petar-stupar/terminalfs/compare/v0.2.0...v0.3.2
[0.2.0]: https://github.com/petar-stupar/terminalfs/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/petar-stupar/terminalfs/releases/tag/v0.1.0
