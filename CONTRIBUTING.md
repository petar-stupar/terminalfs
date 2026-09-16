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
