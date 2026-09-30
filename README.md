# terminalfs

Shell commands as a filesystem, served over 9P. An agent runs a command by writing a file and
reads what it did with `cat`, instead of calling a command tool that blocks until the command ends
and returns all of its output at once.

```text
echo 'dotnet build' > <mount>/ctl/build
cat <mount>/cmd/build/wait
cat <mount>/cmd/build/stdout
```

`<mount>` is where the tree is mounted: the path a plugin gives the agent, or `~/mnt/terminalfs`
for a tree you mount yourself.

Output is a file that grows, so `tail`, `grep` and `wc -l` work on it, a long command is a
directory you look at whenever you like, and several commands run at once because they are several
files. Nothing is on disk: `cmd/build/` is the server's account of the command, rendered when read.

Built on [`ninep`](https://github.com/petar-stupar/9p-csharp), the 9P implementation for .NET.

> **This runs commands as you**, with your shell, environment and credentials. The server binds
> loopback only, and the rules below refuse commands before they run, but none of it is a sandbox.
> See [Permissions](#permissions).

## Install

```sh
curl -fsSL https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.sh | sh
terminalfs --init-settings      # once: writes ~/.config/terminalfs/settings.json, which is required
```

This installs to `~/.local/bin`. On Windows, use
`irm https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.ps1 | iex`. From
source: `dotnet publish src/TerminalFs -c Release -o out`.

## Quick start

With a plugin, each agent session gets a tree of its own, mounted when the session starts and
removed when it ends. The agent is told where the tree is and given a skill for using it, and what
it runs through the tree is held to the session's own permission rules. Sessions need Linux,
`setsid`, and root to mount: either be root, or have `sudo` that asks no password for `mount` and
`umount`, since the mount is made by a server in the background with no terminal to ask on.

### Claude Code

```sh
terminalfs plugin install claude
```

This writes a plugin marketplace to `$XDG_DATA_HOME/terminalfs/claude-code` and prints the
`claude plugin` commands that add it and, later, update it. To follow `main` instead, add the
repository itself:

```sh
claude plugin marketplace add petar-stupar/terminalfs
claude plugin install terminalfs@terminalfs
```

### opencode

```sh
terminalfs plugin install opencode
```

This puts the plugin in `$XDG_CONFIG_HOME/opencode/plugins/terminalfs/`, where opencode v2 finds it
when it next starts. opencode's code mode leaves out the built-in file tools, so the plugin adds
`read` and `write` to it as `file_read` and `file_write`. That lets one `execute` script write a
command and read what it did. The plugin's `codemode` option names other tools to add, or none with
`[]`. To pass it, install the plugin with `--dir <dir>` somewhere opencode does not look on its
own, and name that directory in opencode's configuration:

```json
"plugins": [{ "package": "<dir>", "options": { "codemode": ["read", "write", "grep"] } }]
```

After installing a newer terminalfs, run `terminalfs plugin install` again, and for Claude Code also
`claude plugin marketplace update terminalfs` and `claude plugin update terminalfs@terminalfs`.
Either plugin needs `terminalfs` on the `PATH`; without it, a session carries on with no tree.

### Without a plugin

One shared tree, on Linux or macOS (on Windows, inside WSL):

```sh
terminalfs --mount          # mounts at ~/mnt/terminalfs
terminalfs --unmount        # unmounts, and takes the container down if it mounted through one
```

| Platform | How it mounts |
| --- | --- |
| Linux | 9P directly, or `--mount-docker` through the container (needs Docker and cifs-utils) |
| macOS | a container that mounts the 9P tree and re-exports it over SMB (needs Docker) |
| Windows | not directly; run it inside WSL and mount there |

The tree serves a skill for each agent under `skills/`. For opencode, add `<mount>/skills/opencode`
to `skills.paths`. For Claude Code, copy `<mount>/skills/claude-code/terminalfs/SKILL.md` to
`$CLAUDE_CONFIG_DIR/skills/terminalfs/SKILL.md` (`~/.claude/skills/…` by default). The skills name
the real mount path when the server knows it: when it mounted the tree, or when `--path` says where
you will. Otherwise replace `<mount>` in the copy. Only the settings file gates a shared tree.

## The tree

```text
/index.md               how the tree is laid out
/ctl/<name>             write a command here to run it
/cmd/index.md           every command, with its status
/cmd/<name>/command     the command, as it was written
/cmd/<name>/pid         the process, while there is one
/cmd/<name>/status      running, completed, error or denied
/cmd/<name>/reason      why it was refused, when it was
/cmd/<name>/exitcode    once it has stopped
/cmd/<name>/stdout      what it wrote, as it writes it
/cmd/<name>/stderr      the same for standard error
/cmd/<name>/wait        reading this blocks until it stops
/cmd/<name>/kill        write anything here to end it
/skills/<agent>/terminalfs/SKILL.md   how an agent should use this
```

Every level has an `index.md`: markdown with YAML frontmatter, in
[Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf).

## Running a command

You choose the name: it is the file you write and the directory the results appear in. A command
runs when its file is closed, so it may be several lines, and a name runs **once**: writing it
again fails with `File exists` until its directory is removed.

```sh
cat > <mount>/ctl/tests <<'EOF'
dotnet test --no-build 2>&1 | tail -40
EOF
```

Each command has its own file rather than all of them sharing one, because a mount merges
concurrent writes to one path and commands would be lost. A name that has been taken and never
written to is only held, and `ls ctl` shows it and `rm ctl/<name>` frees it. So a tool that creates
a file before writing it works, and so does one that writes a temporary name and renames it: a
written name waits `--settle` milliseconds (250) before it runs, and runs under the name it ends up
with.

`cat cmd/<name>/wait` blocks until the command stops and prints its state. After 25 seconds it
prints `running`, which means read it again. `stdout` and `stderr` end at what has arrived so far,
so open them fresh each time. `ls cmd/<name>` shows whether a command is still going: `pid` and
`kill` are there while it runs, and `exitcode` once it has stopped.

```sh
echo x > <mount>/cmd/build/kill   # end it, keeping what it produced
rm -r <mount>/cmd/build           # remove it, ending it first if it is still running
```

A finished command is removed on its own once nothing has read it for `--keep` seconds (60).

## Permissions

A command can be refused by two things. The agent's own rules are applied by its plugin, before the
command reaches the tree. The settings file is applied by the server to every tree, whoever writes
to it.

**Claude Code.** Claude Code checks its rules against the Bash call that writes the command, not the
command inside it. The plugin's hook reads the command out of the call and checks it against the
`Bash(...)` rules in the managed, user, project and local settings files: deny, then ask, then
allow, applied to the whole command and to each subcommand. A command that no rule decides follows
the permission mode:

| Mode | A command no rule decides |
| --- | --- |
| `default`, `acceptEdits` | asks |
| `auto` | left to auto mode's classifier, which sees the whole call |
| `bypassPermissions` | runs |
| `dontAsk` | refused |
| `plan` | refused, as everything is until the plan is approved |

Rules given on the command line, a skill's `allowed-tools`, a "yes, for this session" at a prompt,
and managed policy that is not a file are not seen at all. A command they would allow follows the
mode above, and one they would deny is stopped only by a rule in a file.

**opencode.** opencode checks a write to `ctl/<name>` as a file edit, so its `shell` rules never
see the command. The plugin answers that check from the session's `shell` rules (and
`permission.bash`) the way opencode reads them for its shell tool. A deny refuses the write and
names the rule, an ask shows opencode's prompt with the command as the diff, an allow lets it
through, and a command no rule matches is asked about. Inside an `execute` script a refused write
fails with only `Unable to write`, and the rule that refused it is added to the script's result.
Answering "always" to that prompt allows edits, not commands. To stop being asked about a
command, write a `shell` allow rule.

Both plugins also refuse what they cannot read a command out of, an edit anywhere in a tree, and
anything aimed at another session's tree.

**The settings file** has the shape of Claude Code's `settings.local.json`, and only its `Bash(...)`
deny rules count:

```json
{ "permissions": { "deny": ["Bash(sudo:*)", "Bash(git push --force:*)"] } }
```

`Bash(git push:*)` is a prefix that stops at a word, `Bash(rm -rf /*)` is a glob, and `Bash(halt)`
is exact. Each rule is applied to the whole command and to each part, so `cd /tmp && sudo ls` is
refused. The server will not start without the file. It re-reads the file while it runs, and an
edit that does not parse keeps the rules already in force.

A refused command still gets a directory, `cmd/<name>/`, holding only `command`, `status`
(`denied`) and `reason`, which names the rule. The write itself fails with just
`Operation not permitted`, so read `reason` to find out why.

**None of this is a boundary.** The agent runs as you, the same as the server. A shell has many ways
to spell a command (`$(which sudo)` is not `sudo`), and any process of yours can write to any tree.
The rules keep an agent that follows its instructions inside what you wrote for it.

## Limits

- There is no terminal and no standard input. Anything that prompts gets end-of-file, and editors,
  pagers and REPLs cannot run. Pass the flag that avoids the prompt, or pipe the answer in.
- Sessions, and so the plugins, are Linux-only for now.
- A session tree stays until its session ends. `terminalfs session gc --older-than 12h` clears up
  the ones an agent left behind.

## Options

`terminalfs --help` and `terminalfs session --help` list everything. The ones most worth knowing:
`--shell` and `--cwd` set what commands run under and where; `--path` sets where the shared tree is
mounted, and names it in the served skills; `--keep`, `--wait-timeout` and `--settle` change the
timings above. Session trees go in a `terminalfs` directory under `$TERMINALFS_RUNTIME_DIR`, else
`$XDG_RUNTIME_DIR`, else `$XDG_CACHE_HOME` (`~/.cache`). A session is found only under the directory
it was started in, so run `session stop` and `session gc` with the same environment as the agent.

## Build

```sh
dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
```

## Licence

MIT. See [LICENSE](LICENSE).
