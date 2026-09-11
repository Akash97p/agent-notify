// AgentNotify harness for OpenCode (stable V1 plugin API).
//
// What it does: when OpenCode needs a human (permission prompt, question
// tool) or finishes a session (idle / error), it calls the local
// `agentnotify` CLI so the request survives on your desktop and phone.
// The plugin never blocks the session: every send is best-effort,
// time-bounded, and silent on failure.
//
// Install with:  agentnotify install-harness opencode
// Manual install: copy this file to ~/.config/opencode/plugins/agentnotify.js
//   (project scope: <repo>/.opencode/plugins/agentnotify.js)
//   Legacy singular directory ~/.config/opencode/plugin/ also works.
//   Restart OpenCode after copying.
//
// No npm dependencies. Works under Bun (OpenCode) and plain Node.
// Targets the stable V1 plugin shape: a named export returning { event }.
// The V2 beta API (Plugin.define) is deliberately not required here.

const AGENT = "opencode";
const SEND_TIMEOUT_MS = 8000;
const MAX_DETAIL = 500;

let cachedBinary = null;

function projectFromDirectory(directory) {
  try {
    if (!directory) return "opencode";
    const parts = String(directory).replace(/\\/g, "/").split("/").filter(Boolean);
    return parts.length > 0 ? parts[parts.length - 1] : "opencode";
  } catch {
    return "opencode";
  }
}

function shortId(value) {
  if (!value) return "session";
  const s = String(value);
  return s.length > 8 ? s.slice(0, 8) : s;
}

function truncate(value) {
  const s = value === undefined || value === null ? "" : String(value);
  if (s.length <= MAX_DETAIL) return s;
  return s.slice(0, MAX_DETAIL - 1) + "…";
}

function childSession(properties) {
  if (!properties || typeof properties !== "object") return false;
  const parent =
    properties.parentID ?? properties.parentId ?? properties.parentSessionID ?? properties.parentSessionId;
  return !!parent;
}

async function resolveBinary() {
  if (cachedBinary) return cachedBinary;
  const candidates = process.platform === "win32"
    ? ["agentnotify.exe", "agentnotify"]
    : ["agentnotify", "agentnotify.exe"];
  // Prefer an explicit override for testing and unusual PATH layouts.
  const override = process.env.AGENTNOTIFY_BIN;
  const ordered = override ? [override, ...candidates] : candidates;
  const { execFile } = await import("node:child_process");
  for (const bin of ordered) {
    try {
      await new Promise((resolve, reject) => {
        const child = execFile(bin, ["--version"], { timeout: 3000, windowsHide: true }, (err) => {
          if (err) reject(err);
          else resolve();
        });
        child.on("error", reject);
      });
      cachedBinary = bin;
      return bin;
    } catch {
      // Try the next candidate.
    }
  }
  return null;
}

async function send({ title, message, type, priority, key, project }) {
  let bin = null;
  try {
    bin = await resolveBinary();
  } catch {
    bin = null;
  }
  if (!bin) return;
  try {
    const { execFile } = await import("node:child_process");
    const args = [
      "send",
      "--agent", AGENT,
      "--project", project || "opencode",
      "--type", type,
      "--priority", priority,
      "--title", title,
      "--message", message,
    ];
    if (key) args.push("--key", key);
    await new Promise((resolve) => {
      try {
        const child = execFile(bin, args, { timeout: SEND_TIMEOUT_MS, windowsHide: true }, () => resolve());
        child.on("error", () => resolve());
        // stdin/stdout/stderr are ignored: a notification must never pollute the session.
      } catch {
        resolve();
      }
    });
  } catch {
    // Best effort only.
  }
}

function permissionDetail(event) {
  try {
    const p = event.properties || {};
    const tool = p.tool ?? p.action ?? p.permission ?? "";
    const resource = p.resource ?? p.command ?? p.filePath ?? p.path ?? p.url ?? p.query ?? "";
    const combined = [tool, resource].filter(Boolean).join(" ").trim();
    return truncate(combined || event.type);
  } catch {
    return truncate(event.type);
  }
}

export const AgentNotifyPlugin = async (ctx) => {
  const directory = ctx && ctx.directory ? ctx.directory : process.cwd();
  const project = projectFromDirectory(directory);

  return {
    event: async ({ event }) => {
      try {
        if (!event || typeof event.type !== "string") return;
        const type = event.type;
        const props = event.properties || {};

        // Session finished: idle (stable) or status-idle (newer builds).
        if (type === "session.idle") {
          if (childSession(props) && process.env.AGENTNOTIFY_INCLUDE_SUBAGENTS !== "1") return;
          const sid = shortId(props.sessionID ?? props.sessionId ?? props.id);
          await send({
            title: "OpenCode ready for review",
            message: `${project}: session ${sid} is idle.`,
            type: "completed",
            priority: "normal",
            project,
          });
          return;
        }
        if (type === "session.status") {
          const status = String(props.status ?? props.state ?? "").toLowerCase();
          if (status !== "idle" && status !== "completed" && status !== "done") return;
          if (childSession(props) && process.env.AGENTNOTIFY_INCLUDE_SUBAGENTS !== "1") return;
          const sid = shortId(props.sessionID ?? props.sessionId ?? props.id);
          await send({
            title: "OpenCode ready for review",
            message: `${project}: session ${sid} is idle.`,
            type: "completed",
            priority: "normal",
            project,
          });
          return;
        }
        if (type === "session.error") {
          if (childSession(props) && process.env.AGENTNOTIFY_INCLUDE_SUBAGENTS !== "1") return;
          const sid = shortId(props.sessionID ?? props.sessionId ?? props.id);
          const err = truncate(props.error ?? props.message ?? "session error");
          await send({
            title: "OpenCode session error",
            message: `${project}: session ${sid} error: ${err}`,
            type: "error",
            priority: "high",
            project,
          });
          return;
        }

        // Permission prompts (both historical and current event names).
        if (type === "permission.asked" || type === "permission.updated") {
          const sid = shortId(props.sessionID ?? props.sessionId ?? props.id);
          const detail = permissionDetail(event);
          await send({
            title: "OpenCode waiting for approval",
            message: `${project}: approval needed (${detail}).`,
            type: "permission_required",
            priority: "high",
            key: `${project}-${sid}-permission`,
            project,
          });
          return;
        }

        // Question tool: OpenCode surfaces interactive questions as a tool call.
        if (type === "tool.execute.before") {
          const tool = String(props.tool ?? props.toolName ?? inputToolName(event) ?? "").toLowerCase();
          if (tool !== "question" && tool !== "ask_user" && tool !== "askuser") return;
          const sid = shortId(props.sessionID ?? props.sessionId ?? props.id);
          const detail = truncate(props.question ?? props.prompt ?? props.text ?? "a question");
          await send({
            title: "OpenCode question for you",
            message: `${project}: ${detail}`,
            type: "input_required",
            priority: "high",
            key: `${project}-${sid}-question`,
            project,
          });
        }
      } catch {
        // Never break the session for a notification.
      }
    },
  };
};

// Some builds nest the tool name one level deeper; keep the lookup tolerant.
function inputToolName(event) {
  try {
    const p = event.properties || {};
    return p.input?.tool ?? p.payload?.tool ?? p.toolInfo?.name ?? "";
  } catch {
    return "";
  }
}

// Also expose a default export for loaders that prefer it. Both exports
// return the same hooks object; OpenCode uses whichever it discovers.
export default AgentNotifyPlugin;
