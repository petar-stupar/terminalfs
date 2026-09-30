---
name: terminalfs
description: >-
  Run shell commands by writing to a mounted filesystem instead of a command tool. Use
  when you need a command to keep running while you do something else, when its output is
  too large to read in one piece, or when you want to watch it as it goes. Covers running
  a command, reading its output as it grows, waiting for it and killing it.
---

# Running commands through terminalfs

`<mount>` below is where the tree is mounted. Your session context names it; if
nothing does and you read this skill from a tree, it is the path you read it from
without `/skills/opencode/terminalfs/SKILL.md` on the end.

Writing a command to `<mount>/ctl/<name>` runs it; what it did appears under
`<mount>/cmd/<name>/` as ordinary files.

## Run a command — in one tool call

Write the command with the `write` tool, as the whole of `<mount>/ctl/<name>`, with the
name your own — letters, digits, `_`, `-` and `.`, up to 64:

```text
write   path:    <mount>/ctl/build
        content: dotnet build 2>&1 | tail -40
```

With the terminalfs plugin, the write's result also carries what the command did: whether
it `completed` or ended in `error`, its exit code, and its output. That is the whole
command, in one call. If it says the command is still `running`, read
`<mount>/cmd/<name>/wait` again, then `<mount>/cmd/<name>/stdout`.

Without the plugin the result only says the file was written. Read
`<mount>/cmd/<name>/wait` **before** `<mount>/cmd/<name>/stdout`: `wait` blocks until the
command stops and says how it ended, or `running` after about 25 seconds, and `stdout` is a
file that grows, so reading it first silently returns partial output. Where an `execute`
tool can call file tools, do the write and both reads in one script rather than three
calls.

The plugin checks each command against your `shell` permission rules, as if you had run
it with a shell tool, before the write lands. A refusal names the rule; don't reword the
command to get round it. opencode may ask the user first. Write the path out in full: a
write into the tree any other way is refused.

## One name, one command

A name runs once. Sequence **inside** the command, not across names: `a && b | c` is one
command. Use a fresh name for the next one. Nothing runs until the ctl file is closed, so
write it in a single operation. `<mount>/cmd/<name>` does not exist until there is
something to report.

## Keep the command short and the output small

What you generate is the most expensive thing you do, and re-reading output is the second.

- Prefer one `sed -i -e … -e …` or one `awk` over an inline `python3 <<EOF` heredoc: an
  inline script is billed as generated text every time you send it. If you genuinely need
  a script, write it to a file once and re-run it by path.
- Bound the output in the command itself — `| tail -40`, `| head`, `grep -c`, `wc -l`.
- For a large result, page `stdout` with the read tool's range instead of pulling it
  whole.
- Pick one mechanism per task. If you are using terminalfs, do not also search the same
  files with native `grep`/`read` tools; every switch is another turn.

## Watching something long-running

`ls <mount>/cmd/<name>` tells you whether it is still going — `pid` and `kill` are there
while it runs, `exitcode` once it has stopped. Re-read `stdout` for more as it arrives,
opening it fresh each time; a handle held open will not see later output.
`echo x > <mount>/cmd/<name>/kill` ends it and keeps what it produced.

## Do not clean up

Command directories are removed on their own about a minute after the last read, and a
name you never write to frees itself. Do not spend a turn on `rm -r`.

## When a write fails

A mount can only report an error number — `Operation not permitted`, `File exists` — so
look under `<mount>/cmd/<name>/`:

- `command`, `status`, `reason` and nothing else — refused by a rule before it ran.
  `reason` names the rule. There is no output because nothing ran. Reword it and use a
  **different** name; the refused one stays taken until removed.
- `File exists` — the name has already run, refused or not, or someone else is writing it.

There is no terminal and no standard input: anything that prompts gets end-of-file. Pass
the flag that avoids the prompt (`--yes`, `--non-interactive`) or pipe the answer in
inside the command. Editors, pagers and REPLs cannot run here.
