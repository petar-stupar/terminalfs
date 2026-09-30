# Contributing

## How changes land

1. Fork and branch.
2. Make the change, with tests for what it promises.
3. Run the gate below.
4. Open a pull request describing what changed and why.

`main` is protected: nothing is pushed to it directly and only the owner merges.

## The gate

```sh
dotnet build -warnaserror && dotnet test && dotnet format --verify-no-changes
```

CI runs it on Linux, macOS and Windows, and separately publishes the self-contained single-file
shape a release ships — that trips analyzers an ordinary build never reaches, and finding one at
tag time means finding it when it is most expensive.

## Tests

There are not many, and that is the intent. Each pins a promise this program makes, and the name
says which: `ARetriedWriteIsRefusedWithTheSameReason`, not `Write_Retry_Throws`.

A test that depends on something the machine may not have **skips rather than fails**
(`Assert.SkipWhen`). A test that needs a clock uses `FakeTimeProvider`; one that needs a process
starts a real one, because a fake of a process would be testing the fake.

Two suites: `TerminalFs.Core.Tests` drives the model directly, and `TerminalFs.Tests` drives a real
server over a real socket with a real 9P client. The second exists because several things are only
visible from there — a clunk that runs a command, a read that ends rather than waits, a removal
that reaches the registry.

**Some things are only visible from a mount.** Every serious bug in this program so far was found
by mounting it and typing at it, not by the suite: a client merging concurrent writes so that three
commands vanished, a control file whose contents were prepended to the next command, and an
`rm -r` that left the process it was meant to stop still running. If a change touches how a file is
written, read or removed, mount it and try it.

**Some things are only visible from a model.** The hooks read a command out of whatever call an agent
makes, and a model does not write the calls the unit tests do: it starts one with a blank line or a
comment, puts `2>&1` on a read, labels its output with `echo`. After any change to the hooks, the
plugins or the skills, run them for real:

```sh
tests/live/run.sh              # Claude Code, then opencode; or name one
```

It builds this branch, installs both plugins from it into a scratch directory, and gives a small
model one task in a scratch project, once per permission mode: run a failing build through the
tree and read its output in the same call, run an `rm` that a deny rule refuses, and list `ctl/`.

| Scenario | Claude Code | opencode | Expected |
| --- | --- | --- | --- |
| `run` | `bypassPermissions` | rules allow everything but `rm` | the build runs through the tree |
| `ask` | `default` | `*` asks, `rm` is denied | the build asks, and with nobody to answer does not run |
| `acceptEdits` | `acceptEdits` | — | the same as `ask`: it approves edits, not commands |
| `auto` | `auto`, with Sonnet | `--auto`, same rules as `ask` | Claude Code's classifier decides, and either outcome is reported; opencode runs it |

In every scenario `rm` is refused. Claude Code does not offer auto mode with every model and falls
back to `default` without saying so, so the `auto` scenario runs with Sonnet and each Claude Code
run is checked to have been in the mode it asked for.

It prints one line per test, `PASS` or `FAIL`, and a count at the end; in `auto` the classifier's
choice is reported as `INFO`, since either is allowed. The checks read the project, not the model's
account: `build.sh` notes in `.ran` whether it ran inside a session's tree, and `out/`, which the
`rm` would remove, must still be there. Under a failed test it shows the refusals the hooks gave in
that run and where its transcript is, and the transcripts are kept; `TFS_LIVE_VERBOSE=1` shows them
for every run. Claude Code labels a hook's refusal `PreToolUse:Bash hook error:`; that is its
wording for a deny, not the hook failing.

It is not part of the gate: it needs logged-in harnesses, a mount (Linux, root or passwordless
`sudo`), and calls a model, seven runs in all.

Claude Code runs as you are logged in, with the plugin loaded for the session only. opencode v2 runs
from its own home, so it never touches yours; log in there once, with a key:

```sh
H=~/.cache/terminalfs-live-opencode
mkdir -p $H/home $H/config $H/data $H/cache $H/state
HOME=$H/home XDG_CONFIG_HOME=$H/config XDG_DATA_HOME=$H/data XDG_CACHE_HOME=$H/cache \
  XDG_STATE_HOME=$H/state opencode auth login opencode --standalone
TFS_LIVE_OPENCODE=$(command -v opencode) TFS_LIVE_OPENCODE_HOME=$H tests/live/run.sh opencode
```

## What belongs where

`src/TerminalFs.Core` knows about commands, processes, output and the rules that refuse them. It
has no reference to any 9P package and mentions no fid, qid or wire format. `src/TerminalFs` is the
protocol and the mount, and knows nothing about how a command runs. The split is what lets each be
tested without the other.

An `internal` type lives under an `Internal/` directory.

## Documentation

The README and the CHANGELOG are part of the change, not a follow-up. So is the skill in
`TreeText`, which is what an agent actually reads: if the behaviour changed, the sentence
describing it did too.

## Releasing

See [docs/releasing.md](docs/releasing.md).
