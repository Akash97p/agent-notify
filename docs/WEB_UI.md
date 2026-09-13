# Web interface

The broker serves a web interface for everything the Windows Settings window and Notification
Center manage. It runs wherever the broker runs, so macOS and Linux get the same configuration
surface as Windows, and nothing extra is installed.

```bash
agentnotify ui
```

That opens `http://127.0.0.1:47821/ui/` (your configured port) in the default browser. You can also
just open that address yourself. There is no sign-in: like the Windows tray app, the interface
trusts whoever is using this computer.

From another machine, forward the port with the same number on both ends and open the address there:

```bash
ssh -L 47821:127.0.0.1:47821 you@the-machine
```

On Windows the tray menu has **Open in browser…**, which does the same thing.

## What it covers

| Page | What you can do |
| --- | --- |
| Overview | Waiting notifications, open questions, delivery totals, and how the broker is running |
| Attention | Active notifications and history; resolve or dismiss |
| Questions | Answer permissions, choices, and text questions agents are waiting on, or withdraw them |
| Channels | Add, edit, test, and delete all nineteen outbound channels, including connecting a Relay |
| Routes | Decide which notifications reach which channel, and see delivery counts |
| Usage | Read local Claude Code and Codex token totals for 7 days, 30 days, or all history, split by source, model, and day |
| Notifications | API port, history retention, pause, do-not-disturb, toast placement and lifetimes, custom types |
| Sounds | Global and per-type sounds, volume, WAV/MP3 upload, and preview |
| Agents | Install or update the skill for Claude Code, Codex, and OpenCode; the harness command for every host |
| About | Version, data folder, and links |

Toast placement and sounds belong to the Windows app. On macOS and Linux those settings are still
stored, and the page says so, but the platform decides how a notification looks.

An answer given here is the same as one given from a toast, the CLI, or a paired phone: the first
valid answer wins and later ones are refused.

Usage reads the broker user's local session logs. It needs no account key or network connection,
and does not return prompt text or log paths to the browser. Counts are historical token records,
not provider billing or live quota. Cached input is separate from uncached input, and Codex
reasoning is included within output. It currently reads Claude Code and Codex only; source replay
and fork cases can still make totals approximate. There are no cost estimates yet.

## How it stays local

The interface is served on the same loopback listener as the API. It asks for no password, so the
protections are aimed at the one realistic threat: some other web site, open in your browser, trying
to reach it. None of them is visible when you use the page.

- **Only this machine.** The listener binds to `127.0.0.1`, so nothing on the network can connect.
- **Other host names are refused.** Every `/ui` request must name `127.0.0.1`, `localhost`, or
  `[::1]` on the broker's port. A DNS-rebinding page reaches the port under its own host name and
  gets `421 Misdirected Request` before any handler runs.
- **Cross-site changes are refused.** Every state-changing request must carry
  `X-AgentNotify-UI: 1` and, when the browser sends one, a same-origin `Origin`. A form on another
  site cannot set that header, and script on another site cannot send it without a CORS preflight
  the broker never grants.
- **A strict content security policy.** Scripts, styles, and requests come only from the broker
  itself; the page cannot be framed.
- **Credentials are write-only.** A channel's stored secrets are never sent back, only the fact that
  one is stored. Leave a secret field blank to keep it; tick *Remove the stored value* to delete an
  optional one.
- **Relay pairing happens in the broker.** Connect shows the approval code in the page; the
  installation token the Relay issues stays in the broker and is saved only with the profile.
- **Answers bind to what you read.** A question's single-use nonce never reaches the page, and an
  answer carries the request digest of the question as displayed, so an answer cannot land on a
  question that changed after you read it.

## Configuration it does not cover

Some settings have no control, on purpose or for now:

- `authToken` is shown only by `agentnotify token`. It guards the `/v1` API agents use, not this page.
- `maxRequestBodyBytes`, `rateLimitPerSecond`, and `maxMetadataBytes` are file edits.
- *Start with Windows* stays in the tray menu, because the registry is its source of truth.
- Harnesses are installed from a terminal. A harness edits another program's configuration, so the
  page shows the command rather than running it.

## Troubleshooting

- **`This broker has no web interface`** — the broker predates it. Update and restart.
- **`421` or every change refused** — the page was opened under another host name or port, such as
  the machine's LAN address or a tunnel on a different local port. Use `http://127.0.0.1:<port>/ui/`
  with the broker's own port.
- **Port change** — saving a new port takes effect after the broker restarts; the page says so.
