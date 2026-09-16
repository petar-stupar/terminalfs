# terminalfs

Shell commands as a filesystem, served over 9P, so an agent runs one by writing a file and reads
what it did with `cat` — instead of a command tool that blocks until the command finishes and
hands back one lump of output.

```text
echo 'dotnet build' > ~/mnt/terminalfs/ctl/build
cat ~/mnt/terminalfs/cmd/build/wait
cat ~/mnt/terminalfs/cmd/build/stdout
```

Nothing there exists on disk. `ctl/build` is a file that runs what you write to it, and
`cmd/build/` is this server's account of what happened, rendered when you read it.

Built on [`ninep`](https://github.com/petar-stupar/9p-csharp), the 9P2000 / 9P2000.u / 9P2000.L
implementation for .NET.

> **This runs commands as you.** Whatever is written to a control file is executed by your shell,
> with your environment, your working directory and your credentials. The server binds loopback
> and refuses to bind anything else; a deny list refuses some commands before they run. Neither is
> a sandbox. Read [Refusing a command](#refusing-a-command) before pointing anything at this.

## Why a filesystem

A command tool is a poor fit for a command that takes a while. It runs to completion before you
learn anything, its output has to fit in one reply, and watching it as it goes needs a second
mechanism that only some harnesses have.

As files, none of that is special. Output is a file that grows, so `tail -n 40`, `grep` and
`wc -l` work on it and you read as much as you want. A command that takes ten minutes is a
directory you look at whenever you like. Several commands run at once because they are several
files. An agent with filesystem tools needs no new tool to use any of it.

## What it serves

```text
/index.md               how the tree is laid out
/ctl/index.md           how to run something
/ctl/<name>             write a command here to run it
/refused                why the writes that failed, failed
/cmd/index.md           every command, with its status
/cmd/<name>/command     the command, as it was written
/cmd/<name>/pid         the process, while there is one
/cmd/<name>/status      running, completed or error
/cmd/<name>/exitcode    once it has stopped
/cmd/<name>/stdout      what it wrote, as it writes it
/cmd/<name>/stderr      the same for standard error
/cmd/<name>/wait        reading this blocks until it stops
/cmd/<name>/kill        write anything here to end it
/skills/terminalfs/SKILL.md   an agent skill for using this
```

Pages are [Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf):
markdown with YAML frontmatter and an `index.md` at every level.

## Running a command

You choose the name. It is the file you write to and the directory the results appear in.

```sh
echo 'dotnet build' > ~/mnt/terminalfs/ctl/build
```

Nothing runs until the file is closed, so a command is never half-executed and it may be several
lines:

```sh
cat > ~/mnt/terminalfs/ctl/tests <<'EOF'
dotnet test --no-build 2>&1 | tail -40
EOF
```

A name runs **once**. Writing to one that is already a command is refused until its directory is
removed. Sequencing belongs inside a command — `a && b | c` is one command.

### A file per command, not one control file

The name is the path rather than a word inside a single `/ctl`, and that is not a matter of taste.
A shared control file cannot carry concurrent writes through a mount: four callers writing at once
reach the server as **one** write, because macOS smbfs merges writes to a path in its page cache.
Three commands are lost with no error anywhere, which is the worst thing this program could do —
the caller is told their command ran. Separate names are separate files, and a client has nothing
to merge.

The same caching is why a control file reports a length of zero. When `/ctl` answered with help
text, the client read it, laid the command over the front and sent back the whole thing, so what
ran was the command followed by the tail of its own help. A file with no length has nothing to
merge into — and, as a consequence, reading one through an SMB mount returns nothing. That is what
`/ctl/index.md` and `/refused` are for.

## Watching one

`cat cmd/<name>/wait` blocks until the command stops and prints its state. It gives up after
twenty-five seconds and prints `running`, which means read it again — a read that outlived a
client's own patience would be reported as a broken mount rather than as a command still working.

`stdout` and `stderr` end at what has arrived rather than waiting for more, which is what makes
them ordinary files. Open them fresh each time; a handle held open will not see what arrives later.

`ls cmd/<name>` says whether it is still going without reading anything: `pid` and `kill` are there
while it runs, `exitcode` once it has stopped.

## Stopping and clearing up

```sh
echo x > ~/mnt/terminalfs/cmd/build/kill   # end it, keeping what it produced
rm -r ~/mnt/terminalfs/cmd/build           # remove it, ending it first if it is still running
```

A finished command is removed on its own once nothing has read it for `--keep` seconds (60 by
default), so a long session does not fill up with old output. Removing a directory while something
is reading it takes the command out of the tree at once and leaves the bytes until the reader is
done.

## Refusing a command

The server will not start without a settings file. It runs whatever is written to a control file,
and a server doing that under rules nobody wrote is worse than one that did not start.

```sh
terminalfs --init-settings      # writes ~/.config/terminalfs/settings.json
```

The shape is Claude Code's `settings.local.json`, so a list written for an agent harness can be
copied across whole:

```json
{ "permissions": { "deny": ["Bash(sudo:*)", "Bash(git push --force:*)"] } }
```

Three rule shapes: `Bash(git push:*)` is a prefix that stops at a word boundary, `Bash(rm -rf /*)`
is a glob anchored at both ends, and `Bash(halt)` is exact. Each is applied to the command as a
whole **and** to each of its segments, because `cd /tmp && sudo ls` is two commands and a rule that
read only the whole string would let the second through behind the first. Entries for other tools —
`Read(...)`, `WebFetch(...)` — are ignored rather than refused, so the file stays shareable.

The file lives outside the tree, and is read before the socket is bound: a deny list served through
the filesystem it governs would be editable by whatever it exists to restrain. It is re-read while
the server runs, and **an edit that does not parse keeps the rules already in force** — an editor
that truncates before it writes leaves a window in which the file is empty, and empty for a deny
list means everything is permitted.

**It is not a sandbox.** A shell has too many ways of spelling the same thing for a textual list to
be complete; `$(which sudo)` is not `sudo`. It exists to stop an agent doing by accident what
nobody asked for.

### Why refusals are in a file

A refusal arrives at a mounted caller as a number and nothing else — `Invalid argument`,
`Operation not permitted` — because 9P2000.L, which a Linux mount and the SMB bridge both speak,
carries no sentence with an error. The reason is in `/refused`, newest last:

```text
14:00:09  lr2: denied by rule 'Bash(echo allowed*)' in /Users/you/.config/terminalfs/settings.json
```

## Install

```sh
curl -fsSL https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.sh | sh
```

Installs to `~/.local/bin`. On Windows, `irm https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.ps1 | iex`.

### From source

```sh
git clone https://github.com/petar-stupar/terminalfs && cd terminalfs
dotnet publish src/TerminalFs -c Release -o out
```

## Run it

```sh
terminalfs --init-settings
terminalfs --mount-docker
```

| Platform | How it mounts |
| --- | --- |
| Linux | 9P directly, or `--mount-docker` for the bridge |
| macOS | a container that mounts the 9P tree and re-exports it over SMB |
| Windows | not directly; run it inside WSL and mount there |

`--unmount` clears up a mount and its container, and is safe to run when nothing is mounted.
`--shell` and `--cwd` say what commands run under and where; `--keep` how long a finished command
is kept; `--wait-timeout` how long a read of `wait` blocks.

### What it cannot do

There is no terminal and no standard input. A command's stdin is closed at once, so anything that
prompts gets end-of-file rather than waiting, and full-screen programs — an editor, a pager, a
REPL — cannot run here. Pass the flag that avoids the prompt, or pipe the answer in inside the
command.

## Build

```sh
dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
```

## Licence

MIT. See [LICENSE](LICENSE).
