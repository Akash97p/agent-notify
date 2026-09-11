/**
 * AgentNotify extension for Pi.
 *
 * - Notifies on `agent_settled` (session finished work) and on every blocking
 *   user prompt (`ui_prompt_start`: permission gates, confirmations, questions).
 * - Opens one broker interaction per blocking prompt so the question is visible
 *   on desktop/phone, and cancels it when the local dialog closes
 *   (`ui_prompt_end`) to avoid stale phone UI.
 *
 * Notify + capture only: the local dialog still collects the answer. Full
 * remote answering (phone decides, Pi obeys) runs Pi in RPC mode, where these
 * same dialogs become `extension_ui_request` messages — see HARNESS.md.
 *
 * Install: copy to `~/.pi/agent/extensions/agentnotify.ts` (global) or
 * `.pi/extensions/agentnotify.ts` (project, trusted projects only), then
 * `/reload`. Or: `agentnotify install-harness pi`. No npm dependencies.
 */

import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { execFile } from "node:child_process";

const AGENT = "pi";
const SEND_TIMEOUT_MS = 8000;
const MAX_TEXT = 500;

function cliBinary(): string {
  const override = process.env.AGENTNOTIFY_BIN;
  if (override && override.trim()) return override.trim();
  return process.platform === "win32" ? "agentnotify.exe" : "agentnotify";
}

function projectFromCwd(cwd: string | undefined): string {
  try {
    if (!cwd) return AGENT;
    const parts = cwd.replace(/\\/g, "/").split("/").filter(Boolean);
    return parts.length > 0 ? parts[parts.length - 1] : AGENT;
  } catch {
    return AGENT;
  }
}

function truncate(value: unknown): string {
  const s = value === undefined || value === null ? "" : String(value);
  return s.length <= MAX_TEXT ? s : s.slice(0, MAX_TEXT - 1) + "…";
}

function send(
  args: string[],
): void {
  try {
    const child = execFile(
      cliBinary(),
      args,
      { timeout: SEND_TIMEOUT_MS, windowsHide: true },
      () => undefined,
    );
    child.on("error", () => undefined);
  } catch {
    // Best effort only: never break the session for a notification.
  }
}

function notify(type: string, title: string, message: string, key?: string): void {
  const args = [
    "send",
    "--agent", AGENT,
    "--type", type,
    "--title", title,
    "--message", message,
  ];
  if (key) args.push("--key", key);
  send(args);
}

interface PromptState {
  interactionId: string;
}

export default function (pi: ExtensionAPI) {
  const waiting = new Map<string, PromptState>();
  let promptSeq = 0;

  pi.on("agent_settled", async (_event, ctx) => {
    try {
      const project = projectFromCwd(ctx.cwd);
      notify(
        "completed",
        "Pi ready for review",
        `${project}: agent settled.`,
      );
    } catch {
      // Never break the session.
    }
  });

  pi.on("ui_prompt_start", async (event, ctx) => {
    try {
      const project = projectFromCwd(ctx.cwd);
      const kind = typeof event.kind === "string" ? event.kind : "prompt";
      const title = typeof event.title === "string" && event.title ? event.title : "Pi needs you";
      const promptKey = `pi-prompt-${Date.now()}-${promptSeq++}`;
      const prompt = `${project}: ${title} (${kind}). Answer in the Pi session; this notice clears when you do.`;

      notify("permission_required", "Pi waiting for you", prompt, promptKey);

      // Open a broker interaction keyed to this dialog so phone/desktop show
      // the same question; cancel it when the dialog closes.
      const child = execFile(
        cliBinary(),
        [
          "interactions", "request",
          "--kind", "single_choice",
          "--prompt", prompt,
          "--choice", "answered-locally:Answered in Pi",
          "--choice", "dismissed:Dismissed",
          "--agent", AGENT,
          "--project", project,
          "--key", promptKey,
          "--ttl", "600",
        ],
        { timeout: SEND_TIMEOUT_MS, windowsHide: true },
        (error: unknown, stdout: unknown) => {
          try {
            if (error) return;
            const parsed = JSON.parse(String(stdout)) as { id?: string };
            if (parsed && typeof parsed.id === "string") {
              waiting.set(promptKey, { interactionId: parsed.id });
            }
          } catch {
            // Best effort only.
          }
        },
      );
      child.on("error", () => undefined);
    } catch {
      // Never break the session.
    }
  });

  pi.on("ui_prompt_end", async (_event, _ctx) => {
    try {
      // Dialogs close in order; cancel every interaction we still track.
      // A maturing broker supersedes repeats by key, so stray cancels are safe.
      for (const [key, state] of waiting) {
        waiting.delete(key);
        send(["interactions", "cancel", state.interactionId]);
      }
    } catch {
      // Never break the session.
    }
  });

  pi.on("session_shutdown", async (_event, _ctx) => {
    waiting.clear();
  });
}
