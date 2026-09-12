#!/usr/bin/env python3
"""AgentNotify bridge for OpenClaw approvals.

Polls `openclaw approvals pending --json`, opens one broker interaction per
pending approval (desktop toast now, phone via Relay later), waits for the
authenticated human answer, and resolves the approval with the OpenClaw CLI.

Usage:
    agentnotify_openclaw.py watch [--interval SECONDS] [--wait-timeout SECONDS]

Keep it running under your process supervisor (systemd, launchd, tmux) next
to the gateway. Requires the `openclaw` CLI (operator-authenticated) and the
`agentnotify` CLI on PATH (`OPENCLAW_BIN` / `AGENTNOTIFY_BIN` override).

Resolution mapping: broker choice allow-once / allow-always / deny passes
straight through to `openclaw approvals resolve`. Anything unsettled
(expired, cancelled, timeout) is left pending: the gateway keeps waiting
exactly as if this bridge were absent. The bridge never invents an answer.

Only the Python standard library is used.
"""

import json
import os
import shutil
import subprocess
import sys
import time

POLL_INTERVAL_S = 5
WAIT_TIMEOUT_S = 290
CLI_TIMEOUT_S = 15
SEND_TIMEOUT_S = 8
DECISIONS = ("allow-once", "allow-always", "deny")


def find_binary(name, override_env, windows_names):
    override = os.environ.get(override_env)
    candidates = [override] if override else []
    if os.name == "nt":
        candidates += windows_names
    else:
        candidates += [name] + [w for w in windows_names if w != name]
    for candidate in candidates:
        if not candidate:
            continue
        path = shutil.which(candidate)
        if path:
            return path
        if os.path.isabs(candidate) and os.path.isfile(candidate):
            return candidate
    return None


def run_capture(binary, args, timeout):
    try:
        completed = subprocess.run(
            [binary] + args, timeout=timeout,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False)
    except (subprocess.TimeoutExpired, OSError):
        return None
    if completed.returncode != 0:
        return None
    try:
        return (completed.stdout or b"").decode("utf-8", "replace")
    except Exception:
        return None


def pending_approvals(openclaw):
    raw = run_capture(openclaw, ["approvals", "pending", "--json"], CLI_TIMEOUT_S)
    if not raw:
        return []
    try:
        data = json.loads(raw)
    except ValueError:
        return []
    items = []
    if isinstance(data, list):
        items = data
    elif isinstance(data, dict):
        for key in ("approvals", "pending", "items", "results"):
            if isinstance(data.get(key), list):
                items = data[key]
                break
    return [i for i in items if isinstance(i, dict)]


def approval_id(item):
    for key in ("id", "approvalId", "approval_id"):
        value = item.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def approval_text(item):
    parts = []
    for key in ("command", "tool", "summary", "description", "prompt", "session", "origin"):
        value = item.get(key)
        if isinstance(value, str) and value.strip():
            parts.append(value.strip())
    text = " ".join(parts)
    return text[:800] if text else "OpenClaw exec approval"


def broker(cmd, timeout):
    binary = find_binary("agentnotify", "AGENTNOTIFY_BIN", ["agentnotify.exe", "agentnotify"])
    if binary is None:
        return None
    raw = run_capture(binary, cmd, timeout)
    if not raw:
        return None
    try:
        return json.loads(raw)
    except ValueError:
        return None


def notify(title, message, key=None):
    binary = find_binary("agentnotify", "AGENTNOTIFY_BIN", ["agentnotify.exe", "agentnotify"])
    if binary is None:
        return
    cmd = [binary, "send", "--agent", "openclaw", "--type", "permission_required",
           "--priority", "high", "--title", title, "--message", message[:1000]]
    if key:
        cmd += ["--key", key]
    try:
        subprocess.run(cmd, timeout=SEND_TIMEOUT_S,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    except Exception:
        pass


def handle(openclaw, item):
    aid = approval_id(item)
    if not aid:
        return
    text = approval_text(item)
    key = f"openclaw-{aid}"
    created = broker(["interactions", "request", "--kind", "permission",
                      "--prompt", f"OpenClaw approval: {text}",
                      "--choice", "allow-once:Allow once",
                      "--choice", "allow-always:Allow always",
                      "--choice", "deny:Deny",
                      "--agent", "openclaw", "--key", key,
                      "--ttl", str(WAIT_TIMEOUT_S + 60)], CLI_TIMEOUT_S)
    if not isinstance(created, dict) or created.get("status") != "pending":
        return
    iid, digest = created.get("id", ""), created.get("request_digest", "")
    if not iid or not digest:
        return
    notify("OpenClaw waiting for approval", f"Approval {aid}: {text}"[:500], key=key + "-notify")

    settled = broker(["interactions", "wait", iid, "--timeout", str(WAIT_TIMEOUT_S)],
                     WAIT_TIMEOUT_S + 30)
    if not isinstance(settled, dict) or settled.get("status") != "answered":
        return
    choice = str(((settled.get("response") or {}).get("choice_id") or ""))
    if choice not in DECISIONS:
        return
    run_capture(openclaw, ["approvals", "resolve", aid, choice], CLI_TIMEOUT_S)


def watch(interval=POLL_INTERVAL_S):
    openclaw = find_binary("openclaw", "OPENCLAW_BIN", ["openclaw.exe", "openclaw", "openclaw.cmd"])
    if openclaw is None:
        sys.stderr.write("openclaw CLI not found on PATH (set OPENCLAW_BIN to override).\n")
        return 1
    if find_binary("agentnotify", "AGENTNOTIFY_BIN", ["agentnotify.exe", "agentnotify"]) is None:
        sys.stderr.write("agentnotify CLI not found on PATH (set AGENTNOTIFY_BIN to override).\n")
        return 1
    seen = set()
    print(f"agentnotify_openclaw: watching approvals (poll {interval}s). Ctrl+C to stop.", flush=True)
    try:
        while True:
            try:
                for item in pending_approvals(openclaw):
                    aid = approval_id(item)
                    if aid and aid not in seen:
                        seen.add(aid)
                        handle(openclaw, item)
                # Forget settled ids so a re-raised approval with the same id is handled.
                if len(seen) > 1000:
                    seen.clear()
            except Exception as exc:
                sys.stderr.write(f"watch error (continuing): {exc}\n")
            time.sleep(interval)
    except KeyboardInterrupt:
        pass
    return 0


def main(argv):
    if len(argv) >= 2 and argv[1] in ("watch", "--help", "-h", "help"):
        if argv[1] != "watch":
            sys.stdout.write(__doc__ + "\n")
            return 0
        interval = POLL_INTERVAL_S
        for i, arg in enumerate(argv[2:]):
            if arg == "--interval" and i + 1 < len(argv[2:]):
                try:
                    interval = max(1, int(argv[2:][i + 1]))
                except ValueError:
                    pass
        return watch(interval)
    sys.stdout.write(__doc__ + "\n")
    return 0 if len(argv) < 2 else 2


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv))
    except Exception as exc:
        sys.stderr.write(f"fatal: {exc}\n")
        sys.exit(1)
