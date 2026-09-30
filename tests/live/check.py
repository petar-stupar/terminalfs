#!/usr/bin/env python3
"""Checks one live run against what the plugin promises in its scenario.

Usage: check.py claude|opencode <scenario> <transcript> <project>

A model decides what it writes, so these checks look at outcomes, read off the project rather
than the transcript: whether build.sh ran inside the tree (it notes so in .ran), and whether out/,
which a deny rule protects, is still there. From the transcript they take only two things: that no
call in the skill's shape was refused as unreadable, and that an ask says why.

It prints one PASS or FAIL line per test, and the run's refusals only under a failure, or with
TFS_LIVE_VERBOSE=1.
"""
import json
import re
import sys

import os

if len(sys.argv) != 5 or sys.argv[1] not in ("claude", "opencode"):
    sys.exit("usage: check.py claude|opencode <scenario> <transcript.jsonl> <project>\n"
             "run.sh calls this for each run; run it yourself on a run kept with TFS_LIVE_KEEP=1")

harness, scenario, path, project = sys.argv[1:5]
SHAPE = re.compile(r"^cat\s*>\s*\S+/ctl/[A-Za-z0-9_.-]+\s*<<\s*'CMD'\s*$")

calls = []  # (command, result text, is_error)


session_mode = None


def claude():
    global session_mode
    pending = {}
    for line in open(path):
        event = json.loads(line)
        if event.get("type") == "system" and event.get("subtype") == "init":
            session_mode = event.get("permissionMode")
        for part in event.get("message", {}).get("content", []) if event.get("type") in ("assistant", "user") else []:
            if not isinstance(part, dict):
                continue
            if part.get("type") == "tool_use" and part.get("name") == "Bash":
                pending[part["id"]] = part["input"].get("command", "")
            elif part.get("type") == "tool_result" and part.get("tool_use_id") in pending:
                body = part.get("content")
                body = body if isinstance(body, str) else json.dumps(body)
                calls.append((pending.pop(part["tool_use_id"]), body, bool(part.get("is_error"))))


def opencode():
    for line in open(path):
        event = json.loads(line)
        part = event.get("part") or {}
        if event.get("type") != "tool_use" and part.get("type") != "tool":
            continue
        state = part.get("state", {})
        tool = part.get("tool", "")
        given = state.get("input", {})
        command = given.get("content") if tool in ("write", "file_write") else given.get("command") or given.get("code") or json.dumps(given)
        output = state.get("output") or state.get("error") or ""
        calls.append((f"[{tool}] {command}", output if isinstance(output, str) else json.dumps(output), state.get("status") == "error"))


(claude if harness == "claude" else opencode)()

if harness == "claude":
    # Claude Code marks a hook's refusal as an error, and labels a deny "PreToolUse:Bash hook error".
    refusals = [(c, r) for c, r, e in calls if "terminalfs" in r and e]
else:
    # opencode reports a refused write inside an execute script as "Unable to write", with the
    # plugin's reason added as a line of its own, and the script itself as completed.
    refusals = [(c, r) for c, r, e in calls
                if re.search(r"^terminalfs: .*(denied|needs approval|refused|not in this session|cannot)", r, re.M)
                or (e and "terminalfs" in r)]


def first_line(command):
    for line in command.replace("\r\n", "\n").split("\n"):
        if line.strip() and not line.lstrip().startswith("#"):
            return line.strip()
    return ""


# What happened, read off the project rather than the transcript: build.sh notes each run, and
# whether it was inside a session's tree, and out/ is what the task tries to remove.
try:
    runs = open(os.path.join(project, ".ran")).read().split()
except FileNotFoundError:
    runs = []
through_tree = "tree" in runs
rm_held = os.path.exists(os.path.join(project, "out", "keep"))

# A call in the skill's shape is never refused for being unreadable: that is the hook misreading
# the very call the skill teaches. A refusal of what the command itself does — a deny rule, a path
# the model got wrong — has a reason of its own and is the model's to fix, not a failure here.
UNREADABLE = ("reached in a way this check cannot read", "named in a call that does more than read the tree")
misread = [first_line(c) for c, r in refusals if SHAPE.match(first_line(c)) and any(p in r for p in UNREADABLE)]

# What each scenario promises. run: a command no rule refuses runs. ask: a command no rule decides
# waits for a person, and there is none, so it does not run. auto: Claude Code's classifier
# decides, so its outcome is reported and cannot fail; opencode's --auto approves it. Everywhere
# the deny rule holds.
MODES = {
    ("claude", "run"): "bypassPermissions",
    ("claude", "ask"): "default",
    ("claude", "acceptEdits"): "acceptEdits",
    ("claude", "auto"): "auto",
    ("opencode", "run"): "rules allow it",
    ("opencode", "ask"): "rules ask",
    ("opencode", "auto"): "--auto",
}
EXPECTED = {
    ("claude", "run"): True, ("claude", "ask"): False, ("claude", "acceptEdits"): False, ("claude", "auto"): None,
    ("opencode", "run"): True, ("opencode", "ask"): False, ("opencode", "auto"): True,
}
mode = MODES.get((harness, scenario), scenario)
expected = EXPECTED.get((harness, scenario))

tests = []  # (status, name)
if harness == "claude":
    tests.append(("PASS" if session_mode == mode else "FAIL",
                  f"session ran in {mode} mode" + ("" if session_mode == mode else f" (it ran in {session_mode}; the model may not offer it)")))
if expected is True:
    tests.append(("PASS" if through_tree else "FAIL", "the build runs through the tree"))
elif expected is False:
    tests.append(("PASS" if not through_tree else "FAIL", "the build waits for approval and does not run"))
    if harness == "claude":
        tests.append(("PASS" if any("needs approval" in r for c, r in refusals) else "FAIL",
                      "the refusal says it needs approval"))
else:
    tests.append(("INFO", "the classifier " + ("let the build run through the tree" if through_tree else "did not let the build run")))
tests.append(("PASS" if rm_held else "FAIL", "rm is refused by the deny rule"))
tests.append(("PASS" if not misread else "FAIL", "no call in the skill's shape is misread"))

label = f"{harness} / {mode}"
for status, name in tests:
    print(f"  {status}  {label}: {name}")

failed = any(status == "FAIL" for status, name in tests)

tally = os.environ.get("TFS_LIVE_TALLY")
if tally:
    with open(tally, "a") as out:
        for status, name in tests:
            out.write(status + "\n")

# The harness's side of it, only where something failed: every refusal the hooks gave, which
# is where a misread call or a broken rule shows.
if failed or os.environ.get("TFS_LIVE_VERBOSE") == "1":
    print(f"        {len(calls)} shell calls; transcript: {path}")
    for command, result in refusals:
        written = re.search(r"/ctl/[A-Za-z0-9_.-]+", command)
        what = first_line(command) if harness == "claude" else f"{command.split(']')[0]}] {written.group(0) if written else ''}"
        reason = " / ".join(line.strip() for line in result.strip().splitlines() if line.strip())
        print(f"        refused: {what[:110]}\n          because: {reason[:260]}")
    for line in misread:
        print(f"        misread: {line[:110]}")

sys.exit(1 if failed else 0)
