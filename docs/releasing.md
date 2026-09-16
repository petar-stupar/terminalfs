# Releasing

A release is a `v*` tag on `main`. Nothing is published per commit, and the version is decided by
a person rather than derived.

## Cutting one

1. Bump `<Version>` in [`Directory.Build.props`](../Directory.Build.props).
2. In [`CHANGELOG.md`](../CHANGELOG.md), rename `## [Unreleased]` to `## [<version>] — <date>`,
   open a fresh empty `## [Unreleased]` above it, and add the two link definitions at the foot.
   The section is the release notes, verbatim.
3. Merge that through a pull request, as everything else.
4. Tag the merge commit and push the tag:

   ```text
   git tag v<version> && git push origin v<version>
   ```

`.github/workflows/release.yml` then:

- refuses the tag unless it names the version in `Directory.Build.props` **and** the changelog has
  a `## [<version>]` section;
- reruns build and test on Linux, macOS and Windows — a tag can name a commit that never went
  through a pull request, and what it produces is what people install;
- publishes self-contained single-file builds for `linux-x64`, `linux-arm64`, `osx-x64`,
  `osx-arm64`, `win-x64` and `win-arm64`, with a `SHA256SUMS` the install scripts check;
- creates the GitHub release with the changelog section as its notes.

## If a tag has to move

Only while nothing has been published. Check that no release exists
(`gh release view v<version>`), then `git push --delete origin v<version>`, retag and push. Once a
release is out, people have the binaries and their checksums; cut the next patch version instead.

## After it is out

Install it the documented way on a machine that does not have it, and run it:

```text
curl -fsSL https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.sh | sh
```

The scripts read the release's `SHA256SUMS`, so this exercises the artifact names, the checksum
file and the archive layout together — the three things that are only wrong after a release.

Then mount it and type at it. Every serious bug in this program so far was found that way and not
by the suite: run a command, read its output as it grows, start several at once, and `rm -r` one
that is still running.
