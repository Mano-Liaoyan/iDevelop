#!/usr/bin/env python3
# Throwaway: install fake clients for the N1 prototype run. Codex answers each task by its title.
import json, os, sys, stat
run, agent, fix, dotnet = sys.argv[1:5]
bindir = os.path.join(run, 'bin')
gate = os.path.join(run, 'gates', 'never')

def msg(text, item='item_1'):
    return json.dumps({"method": "item/completed", "params": {"item": {"id": item, "type": "agentMessage", "text": text}}})
def tool(command):
    return json.dumps({"method": "item/started", "params": {"item": {"type": "commandExecution", "command": command}}})
def session(sid):
    return json.dumps({"method": "thread/started", "params": {"thread": {"id": sid}}})
done = json.dumps({"method": "turn/completed", "params": {"turn": {"id": "turn-1", "status": "completed"}}})
def failed(message):
    return json.dumps({"method": "turn/completed", "params": {"turn": {"id": "turn-1", "status": "failed", "error": {"message": message}}}})
def prints(*lines):
    return [{"print": line} for line in lines]

report = ("The format is one JSON file per workflow, with tasks, connections, and positions in separate arrays, "
          "and every multi-line text stored as an array of lines so it reads well in a plain text editor.\n\n"
          "## Decisions\n\n- Stable key order, so diffs stay small.\n- Unknown fields fail with their line number.")
question = ("```idevelop\n" + json.dumps({"status": "asking", "question":
    "The rename is not atomic on network drives. Should I add a copy-and-verify fallback for them, or refuse to save there?"}) + "\n```")

options = [
    {"prompt": "# Design the file format\n", "steps": prints(session("a1b2c3d4-0000-4000-8000-00000000000a"),
        tool("rg --files src/Workflows")) + [{"sleep": 1500}] + prints(tool("cat src/Workflows/Workflow.cs")) + [{"sleep": 1500}] +
        [{"write": ["FORMAT.md", "# Workflow format\n"]}] + prints(msg(report), done)},
    {"prompt": "# Write the format parser\n", "steps": prints(session("a1b2c3d4-0000-4000-8000-00000000000b"),
        tool("dotnet test --filter WorkflowFormat"), tool("rg -n \"ReadTask\" src"),
        msg("Three of the five format tests pass. The two left fail on tasks without a position, so I am making the position optional.", "item_0")) +
        [{"waitForFile": gate}] + prints(done)},
    {"prompt": "# Implement atomic save\n", "steps": prints(session("a1b2c3d4-0000-4000-8000-00000000000c"),
        tool("rg -n \"File.WriteAll\" src"), msg("I read how the app writes files today.\n\n" + question), done)},
    {"prompt": "# Write crash tests\n", "steps": prints(session("a1b2c3d4-0000-4000-8000-00000000000e"),
        tool("dotnet test --filter Crash")) + [{"sleep": 2000}] + prints(
        failed("The test host could not start: the crash harness needs a writable temp folder and /tmp/idp-crash is read-only."))},
    {"prompt": "# Integrate storage\n", "steps": prints(session("a1b2c3d4-0000-4000-8000-00000000000d"), msg("Done."), done)},
]

def shim(name, rules):
    rf = os.path.join(bindir, name + '.rules.json')
    json.dump({"rules": rules}, open(rf, 'w'))
    path = os.path.join(bindir, name)
    open(path, 'w').write(f'#!/bin/sh\nexec "{dotnet}" "{agent}" --rules "{rf}" -- "$@"\n')
    os.chmod(path, 0o755)

def rule(when, steps): return {"when": when, "has": [], "thread": None, "threadId": None, "beforeTurnResponse": [], "steps": steps}
shim('codex', [
    rule(["debug", "models"], [{"replay": os.path.join(fix, 'codex-debug-models.json')}]),
    rule(["login", "status"], [{"print": "Logged in using ChatGPT"}]),
    rule(["app-server"], [{"choose": options}]),
])
shim('claude', [rule(["auth", "status"], [{"print": '{"loggedIn":true}'}])])
shim('pi', [])
shim('agy', [])
