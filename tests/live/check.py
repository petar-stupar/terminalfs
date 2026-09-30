#!/usr/bin/env python3
"""Checks one live run against what the plugin promises in its scenario.

Usage: check.py claude|opencode <scenario> <transcript> <project>

A model decides what it writes, so these checks look at outcomes, read off the project rather
than the transcript: whether build.sh ran inside the tree (it notes so in .ran), and whether out/,
which a deny rule protects, is still there. From the transcript they take only two things: that no
call in the skill's shape was refused as unreadable, and that an ask says why. Every refusal the
hooks gave is printed either way, because a new kind of refusal is the thing to look at after
changing the hooks.
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

failures = []

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


print(f"{harness}/{scenario}: {len(calls)} shell calls, {len(refusals)} refused by terminalfs")
for command, result in refusals:
    written = re.search(r"/ctl/[A-Za-z0-9_.-]+", command)
    what = first_line(command) if harness == "claude" else f"{command.split(']')[0]}] {written.group(0) if written else ''}"
    reason = " / ".join(line.strip() for line in result.strip().splitlines() if line.strip())
    print(f"  refused: {what[:100]}\n       -> {reason[:240]}")

# A call in the skill's shape is never refused for being unreadable: that is the hook misreading
# the very call the skill teaches. A refusal of what the command itself does — a deny rule, a path
# the model got wrong — has a reason of its own and is the model's to fix, not a failure here.
UNREADABLE = ("reached in a way this check cannot read", "named in a call that does more than read the tree")
for command, result in refusals:
    if SHAPE.match(first_line(command)) and any(phrase in result for phrase in UNREADABLE):
        failures.append(f"a call in the skill's shape was refused as unreadable: {first_line(command)}")

# What happened, read off the project rather than the transcript: build.sh notes each run, and
# whether it was inside a session's tree, and out/ is what the task tries to remove.
try:
    runs = open(os.path.join(project, ".ran")).read().split()
except FileNotFoundError:
    runs = []
through_tree = "tree" in runs
outside_tree = "shell" in runs
rm_held = os.path.exists(os.path.join(project, "out", "keep"))
asked = any("needs approval" in r for c, r in refusals)

print(f"  build.sh ran: {'through the tree' if through_tree else 'not through the tree'}"
      f"{', and outside it too' if outside_tree else ''}; out/ {'still there' if rm_held else 'REMOVED'}")

# What each scenario promises. run: a command no rule refuses runs, one a rule refuses does not.
# ask: a command no rule decides waits for a person, and there is none. auto: Claude Code's
# classifier decides such a command, so either outcome is reported and neither fails; opencode's
# --auto approves it. Everywhere, a deny rule holds.
expected = {
    ("claude", "run"): True,
    ("claude", "ask"): False,
    ("claude", "acceptEdits"): False,
    ("claude", "auto"): None,
    ("opencode", "run"): True,
    ("opencode", "ask"): False,
    ("opencode", "auto"): True,
}.get((harness, scenario), None)

wanted = {"run": "bypassPermissions", "ask": "default", "acceptEdits": "acceptEdits", "auto": "auto"}.get(scenario)
if harness == "claude" and session_mode != wanted:
    failures.append(f"the session ran in {session_mode} mode, not {wanted}: this model may not offer it")
if not rm_held:
    failures.append("out/ was removed: the rm deny rule did not hold")
if expected is True and not through_tree:
    failures.append("build.sh never ran through the tree")
if expected is False and through_tree:
    failures.append("build.sh ran through the tree though no rule allowed it and nobody could approve it")
if harness == "claude" and expected is False and not asked:
    failures.append("no ask was seen, or it did not say it needs approval")
if outside_tree:
    print("  note: build.sh also ran outside the tree, with a shell tool; the model did not keep to the skill")

for failure in failures:
    print(f"  FAIL: {failure}")
print("  ok" if not failures else f"  {len(failures)} failed")
sys.exit(1 if failures else 0)
