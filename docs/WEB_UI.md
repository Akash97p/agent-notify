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

From another machine, forward a free local port to the broker's loopback listener and open that
local address. For example, if the broker uses 47821 and you want port 47822 on your computer:

```bash
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:47822:127.0.0.1:47821 you@the-machine
```

Open `http://127.0.0.1:47822/ui/` while the SSH command remains running.

On Windows the tray menu has **Open in browser…**, which does the same thing.

## What it covers

| Page | What you can do |
| --- | --- |
| Overview | Waiting notifications, open questions, delivery totals, and how the broker is running |
| Attention | Active notifications and history; resolve or dismiss |
| Questions | Answer permissions, choices, and text questions agents are waiting on, or withdraw them |
| Channels | Add, edit, test, and delete all nineteen outbound channels, including connecting a Relay |
| Routes | Decide which notifications reach which channel, and see delivery counts |
| Dashboard | See live account balances, 30-day tokens and cost, agent mix, usage trend, top projects, OpenCode Go estimates, and broker delivery health together |
| Usage | Read compact local Claude Code, Codex, and OpenCode activity summaries; expand sessions, projects, models, token buckets, and pricing when needed |
| Live quota | Check remaining balances for multiple named Codex and Claude Code profiles; compare a separately labeled local OpenCode Go per-model estimate |
| Notifications | API port, history retention, pause, do-not-disturb, toast placement and lifetimes, custom types |
| Sounds | Global and per-type sounds, volume, WAV/MP3 upload, and preview |
| Agents | Install or update the skill for Claude Code, Codex, and OpenCode; the harness command for every host |
| About | Version, data folder, and links |

Toast placement and sounds belong to the Windows app. On macOS and Linux those settings are still
stored, and the page says so, but the platform decides how a notification looks.

An answer given here is the same as one given from a toast, the CLI, or a paired phone: the first
valid answer wins and later ones are refused.

Usage reads the broker user's local Claude Code/Codex session logs and OpenCode SQLite database.
It needs no account key or network connection,
and does not return prompt text, full project paths, or log paths to the browser. Projects are
grouped by working directory and shown by folder name; same-named folders get distinct opaque IDs.
**Recent sessions** groups deduplicated rows by agent session and project. It shows the latest 50
sessions in the selected period with their time span, token totals, models, and estimated cost;
raw provider session IDs remain on the broker. Records without a usable session ID still count in
overall and project totals but do not appear in that list.
Counts are historical token records, not provider billing or live quota. Cached input is separate
from uncached input. Codex reasoning is included within output; OpenCode reasoning is a separate
counter added to output once. OpenCode's current `message` table is queried on every refresh.
`OPENCODE_DATA_DIR` can point to a custom data directory; otherwise XDG's data home or
`~/.local/share/opencode/opencode.db` is used.

The Insights **Dashboard** combines the existing 30-day Usage, Live quota, and broker Overview
responses in the browser. It does not add account percentages to local token totals: local logs do
not reliably identify which subscription account made each request. Quota cards therefore remain
account-scoped while token and cost charts remain agent/project-scoped. Dashboard animation uses
local CSS only, respects the operating system's reduced-motion preference, and loads no third-party
script or telemetry service.

The cost number answers **what these tokens would cost at published token rates**, using
the dated rate snapshot shown on the page. OpenAI and Claude use standard API rates; OpenCode Go
uses its published quota-equivalent token rates. It is not a subscription charge or invoice. Claude
5-minute and 1-hour cache writes use different rates. The estimate excludes plan allowances,
Fast/Batch pricing, long-context premiums, server-side tool fees, taxes, and discounts. A model
without an exact verified rate is marked *unpriced*; its tokens remain in usage totals but no zero
cost is implied. Current rates are applied to old records, not historical price schedules.
The dated catalog uses published [OpenAI model prices](https://developers.openai.com/api/docs/models)
and [Claude API prices](https://platform.claude.com/docs/en/about-claude/pricing), plus
[OpenCode Go token rates](https://opencode.ai/docs/go/); changing rates
requires a new catalog snapshot.
The Go catalog currently covers 20 exact fixed-rate model IDs. Published cache-write rates are
included for MiniMax M2.7/M2.5 and Qwen3.8 Max/Flash and Qwen3.7 Max. Models with context-length
or time-of-day prices remain unpriced because local records cannot select a verified tier; a
cache-write record is also unpriced when Go publishes no cache-write rate for that model.

Live quota is fetched when its page is opened or **Check now** is pressed. Codex uses its own
documented app-server RPC, so AgentNotify does not read Codex credentials. Claude Code uses its
current local OAuth access token for a read-only call to Anthropic's first-party account-usage
endpoint; AgentNotify does not refresh or store that token. This Claude endpoint is not a stable
public API and can become unavailable. Neither source sends token text, account email, or raw
provider responses to the browser. Snapshots are cached for five minutes; a manual recheck is
limited to once every 30 seconds. A failed check keeps the last known result marked *stale* for
the same credential scope; an account change clears it. Missing windows are never displayed as
0% remaining. Each quota bar represents the balance left and shrinks as usage rises. Each named
profile has its own cache and failure state. A balance of 40% or more uses the neutral bar, 20–39%
uses yellow, and 0–19% uses red. OpenCode has no single quota
because its models can use different provider accounts.
The local Usage page continues to work without internet or signed-in agent accounts.

### Monitor several Codex or Claude Code accounts

The current Codex and Claude Code profiles appear automatically. To add another, open **Live quota
→ Manage accounts**, choose the agent, enter a name and the *agent profile directory*, then
press **Add account**. The directory must be under your home folder. Create it before signing in;
current Codex versions reject a missing `CODEX_HOME`. For macOS/Linux, for example:

```bash
mkdir -p "$HOME/.codex-second" "$HOME/.claude-second"
CODEX_HOME="$HOME/.codex-second" codex login
CLAUDE_CONFIG_DIR="$HOME/.claude-second" claude auth login
```

On Windows PowerShell, set `$env:CODEX_HOME` or `$env:CLAUDE_CONFIG_DIR` to a separate directory
under `$HOME` before running `codex login` or `claude`. These are the agents' own documented profile
switches: [Codex config location](https://learn.chatgpt.com/docs/config-file/config-advanced) and
[Claude Code environment variables](https://code.claude.com/docs/en/env-vars). AgentNotify does not
copy a login or offer a password field. Codex is queried with that profile's `CODEX_HOME`; Claude
Code is queried only when that profile has a readable `.credentials.json`. Claude Code may store
credentials in the macOS Keychain instead, so an extra macOS Claude profile can show unavailable
until its agent-owned credential file is present. Removing a monitored account removes only its
AgentNotify entry, not its agent profile or sign-in. Any current or additional Codex/Claude account
can be renamed in **Manage accounts**; the owner-only config stores the display name and the agent
login is unchanged. Up to 16 additional profiles can be listed.

### OpenCode Go local estimate

OpenCode Go publishes [per-model dollar caps and token rates](https://opencode.ai/docs/go/):
the five-hour cap is 20% and the weekly cap is 50% of that model's monthly cap. The page compares
only usage-bearing OpenCode Go records in this machine's SQLite database against those caps for
rolling last-five-hour, last-seven-day, and last-30-day periods. It currently has exact verified
rates and caps for Muse Spark 1.2/1.3 Contributor and GLM-5.3; other models stay unknown until
priced. A window with an unpriced record has no percentage. This is **not** the Go account's live
remaining quota: usage in other clients or on other machines, multiple Go keys in the same local
database, the actual monthly billing boundary, and provider-side adjustments are unavailable from
the local records. It displays no invented provider reset time or subscription charge.

## How it stays local

The interface is served on the same loopback listener as the API. It asks for no password, so the
protections are aimed at the one realistic threat: some other web site, open in your browser, trying
to reach it. None of them is visible when you use the page.

- **Only this machine.** The listener binds to `127.0.0.1`, so nothing on the network can connect.
- **Other host names are refused.** Every `/ui` request must name `127.0.0.1`, `localhost`, or
  `[::1]`. An SSH forward may use a different port on the browser's computer; its loopback host
  and port are accepted. A DNS-rebinding page reaches the port under its own host name and gets
  `421 Misdirected Request` before any handler runs.
- **Cross-site changes are refused.** Every state-changing request must carry
  `X-AgentNotify-UI: 1` and, when the browser sends one, an `Origin` matching the request's exact
  loopback host and port. A form on another
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
- **`421` or every change refused** — open the page with the browser-side loopback address
  (`http://127.0.0.1:<forwarded-port>/ui/` for an SSH forward). A LAN host name or IP is refused.
- **SSH `connect failed: Connection refused`** — the remote end of `-L` must use the broker's
  listening port, even if the port on your computer differs. For example,
  `-L 127.0.0.1:47822:127.0.0.1:47821` targets a broker listening on 47821.
- **Port change** — saving a new port takes effect after the broker restarts; the page says so.
