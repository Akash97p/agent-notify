"""AgentNotify approval transport for Hermes.

Routes Hermes tool-approval prompts through the local AgentNotify broker
(desktop toast now, phone via Relay later) and waits for the authenticated
human answer, then returns it as the request-bound decision.

Enablement is two explicit consent steps (Hermes design, not ours):

    plugins:
      enabled: [agentnotify]

    security:
      approval:
        transport: agentnotify
        transport_fallback: deny   # or: builtin (show the ordinary prompt on failure)

Transport errors (broker unreachable, timeout, invalid answer) raise, and
Hermes denies by default — a failure can never silently allow a command.
Only the Python standard library is used.
"""

import json
import os
import shutil
import subprocess
import uuid

TRANSPORT_NAME = "agentnotify"
WAIT_TIMEOUT_S = 300
SEND_TIMEOUT_S = 8


def _find_cli():
    override = os.environ.get("AGENTNOTIFY_BIN")
    candidates = [override] if override else []
    if os.name == "nt":
        candidates += ["agentnotify.exe", "agentnotify"]
    else:
        candidates += ["agentnotify", "agentnotify.exe"]
    for candidate in candidates:
        if not candidate:
            continue
        path = shutil.which(candidate)
        if path:
            return path
        if os.path.isabs(candidate) and os.path.isfile(candidate):
            return candidate
    return None


def _run_cli(args, timeout):
    cli = _find_cli()
    if cli is None:
        raise RuntimeError("agentnotify CLI not found on PATH (set AGENTNOTIFY_BIN to override)")
    try:
        completed = subprocess.run(
            [cli] + args,
            timeout=timeout,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(f"agentnotify timed out: {exc}") from exc
    except OSError as exc:
        raise RuntimeError(f"agentnotify failed to start: {exc}") from exc
    if completed.returncode != 0:
        detail = (completed.stderr or b"").decode("utf-8", "replace").strip()[:300]
        raise RuntimeError(f"agentnotify exited {completed.returncode}: {detail}")
    try:
        return json.loads((completed.stdout or b"").decode("utf-8", "replace"))
    except ValueError as exc:
        raise RuntimeError("agentnotify returned unparsable JSON") from exc


def _send_notification(kind, title, message, key=None):
    try:
        cli = _find_cli()
        if cli is None:
            return
        cmd = [cli, "send", "--agent", "hermes", "--type", kind,
               "--title", title, "--message", message]
        if key:
            cmd += ["--key", key]
        subprocess.run(cmd, timeout=SEND_TIMEOUT_S,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
    except Exception:
        pass


def _request_text(request):
    for attr in ("description", "command"):
        try:
            value = getattr(request, attr, None)
        except Exception:
            value = None
        if isinstance(value, str) and value.strip():
            return value.strip()[:1000]
    try:
        mapping = dict(request)
    except Exception:
        mapping = None
    if isinstance(mapping, dict):
        for key in ("description", "command", "prompt"):
            value = mapping.get(key)
            if isinstance(value, str) and value.strip():
                return value.strip()[:1000]
    return "Hermes tool approval"


def _allowed_choices(request):
    for attr in ("allowed_choices", "choices", "allowed"):
        try:
            value = getattr(request, attr, None)
        except Exception:
            value = None
        if isinstance(value, (list, tuple)) and value:
            return [str(v) for v in value if str(v).strip()][:12]
    return ["allow-once", "deny"]


def _host_timeout(request, default=WAIT_TIMEOUT_S):
    for attr in ("timeout", "timeout_seconds", "timeout_secs"):
        try:
            value = getattr(request, attr, None)
        except Exception:
            value = None
        if isinstance(value, (int, float)) and value > 0:
            return int(min(value, 3600))
    return default


def present(request):
    """Approval-transport entry point. Blocks until the human answers."""
    prompt = _request_text(request)
    choices = _allowed_choices(request)
    timeout = _host_timeout(request)

    args = ["interactions", "request", "--kind", "permission",
            "--prompt", prompt, "--agent", "hermes",
            "--ttl", str(max(30, min(timeout, 3600)))]
    for choice in choices:
        args += ["--choice", f"{choice}:{choice.replace('-', ' ').title()}"]
    created = _run_cli(args, SEND_TIMEOUT_S + 5)
    status = str(created.get("status", ""))
    digest = str(created.get("request_digest", ""))
    interaction_id = str(created.get("id", ""))
    if not interaction_id or not digest or status != "pending":
        raise RuntimeError("broker did not open a pending interaction")

    _send_notification("permission_required", "Hermes waiting for approval",
                       prompt[:500], key=f"hermes-{interaction_id[:8]}-approval")

    # Wait slightly less than the host timeout so Hermes, not us, owns the deadline.
    settled = _run_cli(["interactions", "wait", interaction_id,
                        "--timeout", str(max(1, timeout - 2))],
                       timeout + 30)
    if str(settled.get("status", "")) != "answered":
        raise RuntimeError(f"no human answer ({settled.get('status', 'unknown')})")

    response = settled.get("response") or {}
    answer = str(response.get("choice_id", "") or "")
    if not answer:
        raise RuntimeError("broker answer carried no choice")

    respond = getattr(request, "respond", None)
    if not callable(respond):
        raise RuntimeError("host request has no respond() binding")
    return respond(answer)


def _notify_session_end(*args, **kwargs):
    _send_notification("completed", "Hermes session ended",
                       "A Hermes session finished.", key=None)


def _notify_approval_incoming(request, **kwargs):
    _send_notification("permission_required", "Hermes approval incoming",
                       _request_text(request)[:500])


def register(ctx):
    ctx.register_approval_transport(TRANSPORT_NAME, present)
    try:
        ctx.register_hook("on_session_end", _notify_session_end)
    except Exception:
        pass
    try:
        ctx.register_hook("pre_approval_request", _notify_approval_incoming)
    except Exception:
        pass
