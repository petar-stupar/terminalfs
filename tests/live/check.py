#!/usr/bin/env python3
"""Checks one live run's transcript against what the plugin promises.

Usage: check.py claude|opencode <scenario> <transcript>

A model decides what it writes, so these checks look for outcomes rather than exact calls: that
a command ran through the tree, that the deny rule refused `rm`, that no call in the skill's shape
was refused as unreadable, that an ask says why. Every refusal the hook gave is printed either way,
because a new kind of refusal is the thing to look at after changing the hooks.
"""
import json
import re
import sys

if len(sys.argv) != 4 or sys.argv[1] not in ("claude", "opencode"):
    sys.exit("usage: check.py claude|opencode run|ask <transcript.jsonl>\n"
             "run.sh calls this for each run; run it yourself on a transcript kept with TFS_LIVE_KEEP=1")

harness, scenario, path = sys.argv[1], sys.argv[2], sys.argv[3]
SHAPE = re.compile(r"^cat\s*>\s*\S+/ctl/[A-Za-z0-9_.-]+\s*<<\s*'CMD'\s*$")

calls = []  # (command, result text, is_error)


def claude():
    pending = {}
    for line in open(path):
        event = json.loads(line)
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

# A command's state, as Claude Code's call reads it back from wait, as the opencode plugin adds it
# to a write's result, or as an execute script hands it back among its own values.
ran = any(
    not e and re.search(r'^(completed|error)\b|^terminalfs: \S+ (completed|error)|"(completed|error)"', r.strip(), re.M)
    for c, r, e in calls if "/ctl/" in c or "ctl/" in c)

if scenario == "run":
    if not ran:
        failures.append("no command was seen running through the tree")
    if not any("rm" in c and "Bash(rm *)" in r for c, r in refusals) and harness == "claude":
        failures.append("the deny rule Bash(rm *) was never seen refusing rm")
    if harness == "opencode" and not any("rm" in c and "'rm *'" in r for c, r in refusals):
        failures.append("the deny rule rm * was never seen refusing rm")
elif scenario == "ask":
    if not any("needs approval" in r for c, r in refusals):
        failures.append("no ask was seen, or it did not say it needs approval")
    if ran:
        failures.append("a command ran though no rule allowed it and nobody could approve it")
    if not any(not e and "/cmd" in c and "ctl/" not in c for c, r, e in calls):
        failures.append("no read of the tree was seen allowed")

for failure in failures:
    print(f"  FAIL: {failure}")
print("  ok" if not failures else f"  {len(failures)} failed")
sys.exit(1 if failures else 0)
