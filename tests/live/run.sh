#!/usr/bin/env bash
# Runs real agent harnesses with the terminalfs plugins against a scratch project, and checks
# what they did. Run it after any change to the hooks, the plugins or the skills: the unit tests
# pin the shapes we know about, and this is where a model shows the ones we don't.
#
#   tests/live/run.sh [claude] [opencode]      both by default
#
# It is not part of the gate. It needs a real, logged-in harness, calls a model (a few cents a
# run, with a small model), mounts session trees (Linux, root or passwordless sudo for mount), and
# a model can take a different path each time, so read what it prints as well as the verdict.
#
#   TFS_LIVE_CLAUDE_MODEL    Claude Code model, haiku by default
#   TFS_LIVE_CLAUDE_AUTO_MODEL  the model for the auto scenario, sonnet by default: Claude Code
#                            does not offer auto mode with every model
#   TFS_LIVE_OPENCODE        opencode v2 binary; the opencode runs are skipped without it
#   TFS_LIVE_OPENCODE_HOME   a directory opencode v2 is logged in under (its HOME and XDG
#                            directories are below it); see CONTRIBUTING.md
#   TFS_LIVE_OPENCODE_MODEL  opencode model, opencode/claude-haiku-4-5 by default
#   TFS_LIVE_KEEP=1          keep the scratch directory and print where it is
set -uo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
if [ $# -eq 0 ]; then harnesses=(claude opencode); else harnesses=("$@"); fi

echo "building terminalfs"
dotnet build "$repo/src/TerminalFs" -c Debug -warnaserror -v quiet -nologo >/dev/null || { echo "build failed"; exit 1; }
binary="$repo/src/TerminalFs/bin/Debug/net10.0/terminalfs"

scratch=$(mktemp -d "${TMPDIR:-/tmp}/terminalfs-live.XXXXXX")
cleanup() {
    TERMINALFS_RUNTIME_DIR="$scratch/rt" "$binary" session gc --older-than 1s >/dev/null 2>&1
    if [ "${TFS_LIVE_KEEP:-}" = 1 ]; then echo "kept $scratch"; else rm -rf "$scratch"; fi
}
trap cleanup EXIT

# The harness finds this binary, not whichever terminalfs is installed. The settings file stays
# the user's own, which a session will not start without.
mkdir -p "$scratch/bin" "$scratch/rt"
printf '#!/bin/sh\nXDG_CONFIG_HOME=%s exec %s "$@"\n' "${XDG_CONFIG_HOME:-$HOME/.config}" "$binary" > "$scratch/bin/terminalfs"
chmod +x "$scratch/bin/terminalfs"

project() {
    local dir="$scratch/$1"
    mkdir -p "$dir/.claude"
    cat > "$dir/build.sh" <<'SH'
#!/bin/sh
# A pretend build: lots of output, one warning, one error, exit 2. It notes whether it ran
# inside a session's tree, whose commands carry the session's token, for the checks to read.
if [ -n "${TERMINALFS_SESSION_TOKEN:-}" ]; then echo tree >> .ran; else echo shell >> .ran; fi
i=1
while [ $i -le 300 ]; do echo "compiling unit $i"; i=$((i+1)); done
echo "warning: unit 17 is slow"
echo "error: unit 242 failed to link" >&2
echo "build finished with errors"
exit 2
SH
    # out/ is what the task tries to remove, and a deny rule forbids: still there, the rule held.
    mkdir -p "$dir/out" && touch "$dir/out/keep"
    echo '{ "permissions": { "deny": ["Bash(rm *)"] } }' > "$dir/.claude/settings.local.json"
    # opencode's run lets everything else through; its other scenarios ask about the rest. Its
    # agents allow shell commands by default, so asking takes a rule of its own.
    if [ "$1" = opencode-run ]; then
        echo '{ "$schema": "https://opencode.ai/config.json", "permission": { "bash": { "*": "allow", "rm *": "deny" } } }' > "$dir/opencode.json"
    else
        echo '{ "$schema": "https://opencode.ai/config.json", "permission": { "bash": { "*": "ask", "rm *": "deny" } } }' > "$dir/opencode.json"
    fi
    git -C "$dir" init -q
    echo "$dir"
}

prompt="Use the terminalfs skill for every shell command in this task, not a shell tool on its own. 1) Run 'sh build.sh' through your session's terminalfs tree, and in the same call show only the last 5 lines of its output and any lines containing 'error'. 2) Tell me its exit code. 3) Run 'rm -rf out' through the tree. 4) List your tree's ctl directory. Report briefly what happened at each step, including any refusals and their reasons. Do not retry a refused step more than once."

failed=0
skipped=0

for harness in "${harnesses[@]}"; do
    case "$harness" in
    claude)
        command -v claude >/dev/null || { echo "claude: not installed, skipped"; skipped=1; continue; }
        PATH="$scratch/bin:$PATH" terminalfs plugin install claude --dir "$scratch/claude-plugin" >/dev/null
        # run: everything no rule refuses runs. ask: a command no rule decides asks, and -p cannot
        # answer. acceptEdits approves edits, not commands, so it asks too. auto leaves such a
        # command to Claude Code's classifier.
        for scenario in run:bypassPermissions ask:default acceptEdits:acceptEdits auto:auto; do
            name=${scenario%%:*} mode=${scenario#*:}
            dir=$(project "claude-$name")
            # Auto mode is not offered with every model, and Claude Code falls back to the
            # default mode without saying so; check.py makes sure the mode asked for is the one
            # the session ran in.
            model=$([ $name = auto ] && echo "${TFS_LIVE_CLAUDE_AUTO_MODEL:-sonnet}" || echo "${TFS_LIVE_CLAUDE_MODEL:-haiku}")
            (cd "$dir" && PATH="$scratch/bin:$PATH" TERMINALFS_RUNTIME_DIR="$scratch/rt" timeout 600 \
                claude -p --model "$model" --plugin-dir "$scratch/claude-plugin/plugins/terminalfs" \
                --permission-mode "$mode" --output-format stream-json --verbose "${prompt//terminalfs skill/terminalfs:terminalfs skill}" \
                < /dev/null > "$scratch/claude-$name.jsonl" 2> "$scratch/claude-$name.err")
            python3 "$here/check.py" claude "$name" "$scratch/claude-$name.jsonl" "$dir" || failed=1
        done
        ;;
    opencode)
        if [ -z "${TFS_LIVE_OPENCODE:-}" ] || [ -z "${TFS_LIVE_OPENCODE_HOME:-}" ]; then
            echo "opencode: skipped; set TFS_LIVE_OPENCODE (an opencode v2 binary) and TFS_LIVE_OPENCODE_HOME"
            echo "          (a home it is logged in under). CONTRIBUTING.md says how to log in once."
            skipped=1
            continue
        fi
        oc="$TFS_LIVE_OPENCODE_HOME"
        rm -rf "$oc/config/opencode/plugins/terminalfs"
        PATH="$scratch/bin:$PATH" terminalfs plugin install opencode --dir "$oc/config/opencode/plugins/terminalfs" >/dev/null
        # run: the project allows every command but rm. ask: only rm is ruled on, so the rest
        # asks, and a run with nobody to answer rejects it. auto: the same rules with --auto,
        # which approves what no rule denies.
        for name in run ask auto; do
            dir=$(project "opencode-$name")
            auto=$([ $name = auto ] && echo --auto || true)
            (cd "$dir" && HOME="$oc/home" XDG_CONFIG_HOME="$oc/config" XDG_DATA_HOME="$oc/data" XDG_CACHE_HOME="$oc/cache" \
                XDG_STATE_HOME="$oc/state" PATH="$scratch/bin:$PATH" TERMINALFS_RUNTIME_DIR="$scratch/rt" timeout 600 \
                "$TFS_LIVE_OPENCODE" run --standalone $auto --format json -m "${TFS_LIVE_OPENCODE_MODEL:-opencode/claude-haiku-4-5}" "$prompt" \
                < /dev/null > "$scratch/opencode-$name.jsonl" 2> "$scratch/opencode-$name.err")
            python3 "$here/check.py" opencode "$name" "$scratch/opencode-$name.jsonl" "$dir" || failed=1
        done
        ;;
    *) echo "unknown harness '$harness'"; failed=1 ;;
    esac
done

if [ $failed -ne 0 ]; then
    echo "FAILED: see the refusals above; TFS_LIVE_KEEP=1 keeps the transcripts"
elif [ $skipped -ne 0 ]; then
    echo "passed, with a harness skipped"
else
    echo "passed"
fi

exit $failed
