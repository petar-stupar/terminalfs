---
name: terminalfs
description: >-
  Run shell commands by writing them to a mounted filesystem with the Bash tool, one call
  per command. Use when you need a command to keep running while you do something else,
  when its output is too large to read in one piece, or when you want to watch it as it
  goes. Covers running a command, reading its output as it grows, waiting for it and
  killing it.
---

# Running commands through terminalfs

`<mount>` below is where the tree is mounted. Your session context names it; if
nothing does, ask rather than guess.

Writing a command to `<mount>/ctl/<name>` runs it; what it did appears under
`<mount>/cmd/<name>/` as ordinary files.

## Run a command — in one Bash call

Write the command and read what it did in the same Bash call, in exactly this shape:

```sh
cat > <mount>/ctl/build <<'CMD'
dotnet build 2>&1 | tail -40
CMD
cat <mount>/cmd/build/wait; cat <mount>/cmd/build/stdout
```

Never split it across calls: every extra call re-reads the whole conversation, so a
command should cost one call, not two or three.

Keep to the shape — `cat >` into `ctl/<name>`, the command in a heredoc quoted as
`'CMD'`, then reads under `cmd/<name>/` — rather than `echo … >` or the Write tool. It is
the shape a permission check can read the command out of: the terminalfs plugin checks it
against your permission rules as if you had run it with Bash, and refuses a write into
the tree in any other shape, or anything after it in the same call but reads of this
tree. The command can be several lines, but none of them can be just `CMD`. Write the
path out in full each time, not through a variable or after a `cd`: the check cannot read
a command written to a path it cannot see, and refuses the ones it notices.

A refusal says why. `denied by` names the rule and the settings file it is in; don't
reword the command to get round it.

Read `wait` **before** `stdout`. `wait` blocks until the command stops and prints
`completed`, `error`, or — after about 25 seconds — `running`; `stdout` is a file that
grows, so reading it first silently returns partial output. Put `2>&1` in the command
rather than reading `stderr` separately, and add `; cat <mount>/cmd/build/exitcode` to the
last line when the number matters.

`running` means it is still going. Read again, still in one call:
`cat <mount>/cmd/build/wait; cat <mount>/cmd/build/stdout`.

## One name, one command

A name runs once. Sequence **inside** the command, not across names: `a && b | c` is one
command. Give the next one a name you have not used yet in this session — letters,
digits, `_`, `-` and `.`, up to 64 — not `build` again. Nothing runs until the ctl file is
closed, and `<mount>/cmd/<name>` does not exist until there is something to report.

## Keep the command short and the output small

What you generate is the most expensive thing you do, and re-reading output is the second.

- Bound the output in the command itself — `| tail -40`, `| head`, `grep -c`, `wc -l`.
- Prefer one `sed -i -e … -e …` or one `awk` over an inline `python3` script: an inline
  script is billed as generated text every time you send it. If you genuinely need a
  script, write it to a file once and re-run it by path.

## Watching something long-running

`ls <mount>/cmd/<name>` tells you whether it is still going — `pid` and `kill` are there
while it runs, `exitcode` once it has stopped. Read `stdout` again for more as it
arrives; `tail -n 40` works on it. `echo x > <mount>/cmd/<name>/kill` ends it and keeps
what it produced.

## Do not clean up

Command directories are removed on their own about a minute after the last read, and a
name you never write to frees itself. Do not spend a call on `rm -r`.

## When a write fails

A mount can only report an error number — `Operation not permitted`, `File exists` — and
the `cat >` fails with it. **When it does, what the reads after it print is not your
command's.** Look under `<mount>/cmd/<name>/`:

- `command`, `status`, `reason` and nothing else — refused by a rule before it ran.
  `cat <mount>/cmd/<name>/reason` names the rule. There is no output because nothing
  ran. Reword it and use a **different** name; the refused one stays taken until removed.
- `File exists` — the name has already run, refused or not, or someone else is writing it.
  The reads print that earlier command's state and output. Run yours again under a name
  you have not used.

There is no terminal and no standard input: anything that prompts gets end-of-file. Pass
the flag that avoids the prompt (`--yes`, `--non-interactive`) or pipe the answer in
inside the command. Editors, pagers and REPLs cannot run here.
