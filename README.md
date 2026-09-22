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
/skills/terminalfs/SKILL.md   an agent skill for using this
```

`SKILL.md` names the mountpoint outright when this server was told one — either because it did
the mounting, or because `--path` said where you would. Otherwise it writes `<mount>` for you to
replace, because a path nobody stated would be a guess, and a skill naming a directory that is
not there is worse than one that asks to be filled in.

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

A name runs **once**. Once it has run, taking it again is refused until its directory is removed.
Sequencing belongs inside a command — `a && b | c` is one command.

### A name is only taken until something decides it

`/cmd/<name>/` does not exist until there is a command to describe. Until the file is closed with
a command in it, `/ctl/<name>` is a name you hold and nothing more — so a name you take and never
write to leaves nothing behind, and `ls ctl` shows the ones in flight while `rm ctl/<name>` gives
one back.

That is what lets you write the file however your tools write files. Creating it first and writing
to it afterwards works, and so does writing to a temporary name and renaming it into place:

```sh
echo 'dotnet build' > ~/mnt/terminalfs/ctl/build.tmp
mv ~/mnt/terminalfs/ctl/build.tmp ~/mnt/terminalfs/ctl/build
```

The command runs as `build`, never as `build.tmp`. A client that writes atomically closes the
temporary file *before* it renames, so running on that close would run the command under a name
you never chose; instead a name that has been written to waits `--settle` milliseconds (250 by
default) before it runs, and anything that looks under `/cmd` runs it at once rather than waiting.
`--settle 0` runs it at the close.

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
`/ctl/index.md` is for.

## Watching one

`cat cmd/<name>/wait` blocks until the command stops and prints its state. It gives up after
twenty-five seconds and prints `running`, which means read it again — a read that outlived a
client's own patience would be reported as a broken mount rather than as a command still working.

`stdout` and `stderr` end at what has arrived rather than waiting for more, which is what makes
them ordinary files. Open them fresh each time; a handle held open will not see what arrives later.

`ls cmd/<name>` says whether it is still going without reading anything: `pid` and `kill` are there
while it runs, `exitcode` once it has stopped. A directory holding only `command`, `status` and
`reason` is one that was refused before it ran; there is no output because nothing ran.

## Stopping and clearing up

```sh
echo x > ~/mnt/terminalfs/cmd/build/kill   # end it, keeping what it produced
rm -r ~/mnt/terminalfs/cmd/build           # remove it, ending it first if it is still running
```

A finished command is removed on its own once nothing has read it for `--keep` seconds (60 by
default), so a long session does not fill up with old output. A name taken and never written to is
freed on the same clock. Removing a directory while something
is reading it takes the command out of the tree at once and leaves the bytes until the reader is
done. A refused command is kept and cleared the same way, with the clock starting at the write that
failed.

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

A refused command still gets its directory. The write to `/ctl/<name>` fails, and `/cmd/<name>/`
appears holding three files — `command`, what you wrote; `status`, which reads `denied`; and
`reason`, which names the rule. Nothing else is there, because nothing ran: a `stdout` on a command
that never started would be a file promising output that can never arrive. The name is spent until
the directory is removed, which is the rule every other command already follows.

**It is not a sandbox.** A shell has too many ways of spelling the same thing for a textual list to
be complete; `$(which sudo)` is not `sudo`. It exists to stop an agent doing by accident what
nobody asked for.

### Where the reason is

A refusal arrives at a mounted caller as a number and nothing else — `Operation not permitted`,
`File exists` — because 9P2000.L, which a Linux mount and the SMB bridge both speak, carries no
sentence with an error. So the reason is put where the caller can walk to it.

```sh
$ echo 'sudo ls' > ~/mnt/terminalfs/ctl/lr2      # Operation not permitted
$ ls ~/mnt/terminalfs/cmd/lr2
command  status  reason
$ cat ~/mnt/terminalfs/cmd/lr2/reason
denied by rule 'Bash(sudo:*)' in /Users/you/.config/terminalfs/settings.json
```

`File exists` is the one refusal with nowhere to write a sentence: the name belongs to a command
that is already there, or to somebody who is writing one now. `ls cmd/<name>` is the answer when
it has run — the directory exists, which is why the name was not free — and `ls ctl` shows it if
it is still being written. Remove it, or pick another name.

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
`--shell` and `--cwd` say what commands run under and where; `--keep` how long a finished command,
or a name nobody wrote to, is kept; `--wait-timeout` how long a read of `wait` blocks; `--settle`
how long a name that has been written to waits before it runs. `--path` says where the tree goes,
and states it for the served skill even when you mount it yourself.

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
