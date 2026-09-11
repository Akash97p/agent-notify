#!/usr/bin/env python3
"""AgentNotify hook bridge for Codex, Claude Code, Gemini CLI, Copilot CLI, Cursor, and Muse Code.

Called by the host's hook system (stdin JSON + argv event name). It sends
one best-effort `agentnotify send` and always exits 0 so a notification
failure can never block the coding session.

Usage:
    agentnotify_hook.py <codex|claude|gemini|copilot|cursor|muse> <event> [--project NAME]

<event> depends on the host (each host only wires its own names):
  codex:   permission, stop, session-end
  claude:  notification, stop
  gemini:  notification, after-agent, session-end
  copilot: notification, agent-stop, session-end, error-occurred
  cursor:  stop, session-end
  muse:    permission-request, stop
Stdin is the host's hook JSON payload (up to 64 KiB); unreadable or
unexpected shapes fall back to generic text rather than failing.

Install with:  agentnotify install-harness <agent>
Only the Python standard library is used.
"""

import json
import os
import shutil
import subprocess
import sys

SEND_TIMEOUT_S = 8
MAX_TEXT = 1000
AGENTS = {
    "codex": "Codex",
    "claude": "Claude Code",
    "gemini": "Gemini CLI",
    "copilot": "Copilot CLI",
    "cursor": "Cursor",
    "muse": "Muse Code",
}


def read_payload():
    try:
        raw = sys.stdin.read(65536)
    except Exception:
        return {}
    if not raw or not raw.strip():
        return {}
    try:
        data = json.loads(raw)
    except Exception:
        return {}
    return data if isinstance(data, dict) else {}


def first_str(payload, *keys):
    for key in keys:
        try:
            value = payload.get(key)
        except AttributeError:
            continue
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def nested_str(payload, *paths):
    for path in paths:
        current = payload
        ok = True
        for part in path:
            if isinstance(current, dict) and part in current:
                current = current[part]
            else:
                ok = False
                break
        if ok and isinstance(current, str) and current.strip():
            return current.strip()
    return ""


def project_from_cwd(cwd, fallback):
    try:
        if not cwd:
            return fallback
        norm = cwd.replace("\\", "/").rstrip("/")
        base = norm.split("/")[-1] if norm else ""
        return base or fallback
    except Exception:
        return fallback


def truncate(text):
    if len(text) <= MAX_TEXT:
        return text
    return text[: MAX_TEXT - 1] + "…"


def find_binary():
    override = os.environ.get("AGENTNOTIFY_BIN")
    candidates = [override] if override else []
    if os.name == "nt":
        candidates += ["agentnotify.exe", "agentnotify"]
    else:
        candidates += ["agentnotify", "agentnotify.exe"]
    for candidate in candidates:
        if not candidate:
            continue
        path = shutil.which(candidate) or (candidate if os.path.isabs(candidate) and os.path.isfile(candidate) else None)
        if path:
            return path
    return None


def send(agent_id, project, ntype, priority, title, message, key=None):
    binary = find_binary()
    if not binary:
        return
    cmd = [
        binary,
        "send",
        "--agent", agent_id,
        "--project", project,
        "--type", ntype,
        "--priority", priority,
        "--title", title,
        "--message", truncate(message),
    ]
    if key:
        cmd += ["--key", key]
    try:
        subprocess.run(
            cmd,
            timeout=SEND_TIMEOUT_S,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    except Exception:
        pass


def main(argv):
    if len(argv) < 3 or argv[1] in ("-h", "--help", "help"):
        sys.stdout.write(__doc__ + "\n")
        return 0
    agent_id = argv[1].lower()
    event = argv[2].lower().replace("_", "-")
    project_override = None
    for i, arg in enumerate(argv[3:]):
        if arg == "--project" and i + 1 < len(argv[3:]):
            project_override = argv[3:][i + 1].strip() or None

    if agent_id not in AGENTS:
        return 0
    label = AGENTS[agent_id]
    payload = read_payload()

    session = (
        first_str(payload, "session_id", "sessionId", "sessionID")
        or nested_str(payload, ("session", "id"), ("context", "session_id"))
        or "session"
    )
    sid = session[:8] if session != "session" else "session"
    cwd = first_str(payload, "cwd", "working_directory", "workingDirectory") or nested_str(
        payload, ("context", "cwd"), ("tool_input", "cwd")
    )
    if not cwd:
        try:
            cwd = os.getcwd()
        except Exception:
            cwd = ""
    project = project_override or project_from_cwd(cwd, agent_id)

    tool = (
        first_str(payload, "tool_name", "toolName", "tool")
        or nested_str(payload, ("tool_input", "tool"), ("tool", "name"))
    )
    message_text = (
        first_str(payload, "message", "text", "notification", "prompt")
        or nested_str(payload, ("tool_input", "command"), ("tool_input", "cmd"))
        or nested_str(payload, ("hookSpecificOutput", "message"),)
    )
    detail = " ".join(part for part in (tool, message_text) if part).strip()

    if event in ("permission", "permissionrequest", "permission-request", "notification"):
        title = f"{label} waiting for approval"
        body = f"{project}: approval needed"
        if detail:
            body += f" ({detail[:500]})"
        else:
            body += "."
        send(agent_id, project, "permission_required", "high", title, body,
             key=f"{project}-{sid}-permission")
    elif event in ("stop", "session-end", "sessionend", "completed",
                   "after-agent", "afteragent", "agent-stop", "agentstop"):
        title = f"{label} ready for review"
        body = f"{project}: session {sid} finished."
        if detail and event == "stop":
            # Keep stop messages short; the transcript stays in the host.
            pass
        send(agent_id, project, "completed", "normal", title, body)
    elif event in ("error", "failure", "error-occurred", "erroroccurred"):
        title = f"{label} session error"
        body = f"{project}: session {sid} reported an error."
        if detail:
            body += f" {detail[:500]}"
        send(agent_id, project, "error", "high", title, body)
    else:
        # Unknown event names are ignored silently: hosts add events often.
        pass
    return 0


if __name__ == "__main__":
    # The host owns the waiter; this bridge must never fail it.
    try:
        sys.exit(main(sys.argv))
    except Exception:
        sys.exit(0)
