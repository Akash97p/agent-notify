# Web interface

The broker serves a web interface for everything the Windows Settings window and Notification
Center manage. It runs wherever the broker runs, so macOS and Linux get the same configuration
surface as Windows, and nothing extra is installed.

This guide describes the current development source. The tagged `v0.1.0-alpha.2` binaries predate
the WebUI Insights pages; build the current `main` branch or wait for the next tagged release to
use Usage, Live quota, and Dashboard.

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

On Windows, right-click the tray icon and choose **Web interface…**, which does the same thing.

## What it covers

| Page | What you can do |
| --- | --- |
| Overview | Waiting notifications, open questions, delivery totals, and how the broker is running |
| Attention | Active notifications and history; resolve or dismiss |
| Questions | Answer permissions, choices, and text questions agents are waiting on, or withdraw them |
| Channels | Add, edit, test, and delete all nineteen outbound channels, including connecting a Relay |
| Routes | Decide which notifications reach which channel, and see delivery counts |
| Model router → Providers | Turn the router on, see its base URLs and key, and manage upstream providers |
| Model router → Routing | Aliases, ordered failover combos, and the default route |
| Model router → Agents | Connect Codex or Claude Code so their own `/model` menu lists routed models, set subagent and review models, disconnect, and restore a saved copy of their configuration |
| Model router → Activity | The router's request ledger and per-model totals |
| Dashboard | See live account balances, 30-day tokens and cost, agent mix, usage trend, top projects, OpenCode Go estimates, and broker delivery health together |
| Usage | Read compact local Claude Code, Codex, OpenCode, Kilo CLI, Muse Code, and Gemini CLI activity summaries (including WSL on Windows); expand sessions, projects, models, token buckets, and pricing when needed |
| Live quota | Check remaining balances for every detected or added Codex and Claude Code profile; see API account balances and spend; compare a separately labeled local OpenCode Go per-model estimate that can follow your billing cycle |
| Notifications | API port, history retention, pause, do-not-disturb, toast placement and lifetimes, custom types |
| Sounds | Global and per-type sounds, volume, WAV/MP3 upload, and preview |
| Agents | Install or update the skill for Claude Code, Codex, and OpenCode; the harness command for every host |
| About | Version, data folder, and links |

The **Model router** pages configure the opt-in local provider proxy described in
[ROUTER.md](ROUTER.md). The router key is shown once when it is generated or regenerated and never
returned afterwards; `agentnotify router key` prints it from the local config. Upstream provider keys
are write-only in exactly the same way as channel secrets: the field stays blank when editing, and no
response carries a stored key.

**Agents** is the one page in this interface that writes files belonging to other programs: Codex's
`config.toml` and Claude Code's `settings.json`. It only does so when you press Connect, Apply,
Disconnect, or Restore. Each of those first copies the file, and the page lists every copy with the
reason it was taken, so any of them can be put back. If you would rather configure an agent yourself,
the same page shows the snippets to copy.

**Activity** shows proxy-observed records: they count only what went through the router. The same
call also appears in the agent's own log, which is what the Usage page reads, so the two must never
be added together.

Toast placement and sounds belong to the Windows app. On macOS and Linux those settings are still
stored, and the page says so, but the platform decides how a notification looks.

An answer given here is the same as one given from a toast, the CLI, or a paired phone: the first
valid answer wins and later ones are refused.

Usage reads the broker user's local Claude Code/Codex session logs, the OpenCode and Kilo CLI SQLite
databases, Muse Code sessions, and Gemini CLI chats.
It needs no account key or network connection,
and does not return prompt text, full project paths, or log paths to the browser. Projects are
grouped by working directory and shown by folder name; same-named folders get distinct opaque IDs.
**Recent sessions** groups deduplicated rows by agent session and project. It shows the latest 50
sessions in the selected period with their time span, token totals, models, and estimated cost;
raw provider session IDs remain on the broker. Records without a usable session ID still count in
overall and project totals but do not appear in that list.
Counts are historical token records, not provider billing or live quota. Cached input is separate
from uncached input. Codex reasoning is included within output; OpenCode reasoning is a separate
counter added to output once. OpenCode's `message` table is queried again only when its database or
write-ahead log has changed; a database on the WSL share is read from a private local copy, deleted
straight after, because SQLite's page-by-page reads over the share are very slow.
`OPENCODE_DATA_DIR` can point to a custom data directory; otherwise XDG's data home or
`~/.local/share/opencode/opencode.db` is used. Codex and Claude Code profiles added by hand on Live
quota are read too (`sessions`/`archived_sessions` and `projects` under each profile directory), so a
second account's history counts; a directory that is also discovered is read once.
Muse Code sessions are read from `$XDG_DATA_HOME/muse/sessions` (default `~/.local/share/muse/sessions`),
one `model_completed` event per model call, with subagent calls grouped under their parent session and
project. The Kilo CLI's `~/.local/share/kilo/kilo.db` uses OpenCode's schema and is read the same way.
Gemini CLI chats come from `~/.gemini/tmp/<project>/chats/session-*.json`; the project folder is
resolved through `~/.gemini/projects.json` when it lists it. Antigravity, Cursor, Windsurf, Kiro,
GitHub Copilot, and the Kilo and Cline IDE extensions keep no readable per-request token history on
disk, so they do not appear in Usage.
On Windows, Usage, Live quota, and Agents also include each running WSL distribution: its agents'
default log locations, a Codex/Claude Code account for each profile directory that exists, and skill
rows labelled `WSL · <distribution>`. Stopped distributions are not read, because opening their
files would start them.

The Insights **Dashboard** combines the existing 30-day Usage, Live quota, and broker Overview
responses in the browser. It does not add account percentages to local token totals: local logs do
not reliably identify which subscription account made each request. Quota cards therefore remain
account-scoped while token and cost charts remain agent/project-scoped. Dashboard animation uses
local CSS only, respects the operating system's reduced-motion preference, and loads no third-party
script or telemetry service.

The cost number answers **what these tokens would cost at published token rates**, using
the dated rate snapshot shown on the page. Each record is priced at its provider's standard API rate:
[OpenAI](https://developers.openai.com/api/docs/pricing),
[Anthropic](https://platform.claude.com/docs/en/about-claude/pricing),
[Meta](https://developer.meta.com/ai/products/meta-model-api/) (Muse Spark, including Contributor),
[Google Gemini](https://ai.google.dev/gemini-api/docs/pricing), [Z.ai](https://docs.z.ai/guides/overview/pricing),
and [Xiaomi MiMo](https://mimo.mi.com/docs/en-US/price/pay-as-you-go). OpenCode Go uses its
[published quota-equivalent token rates](https://opencode.ai/docs/go/), and the free models of
[OpenCode Zen](https://opencode.ai/docs/zen/) (`-free`) and Kilo (`:free`) cost nothing. It is not a
subscription charge or invoice. The provider comes from the agent (Codex → OpenAI, Claude Code →
Anthropic) or, for OpenCode, from each message's provider ID.

Rates vary per record where the provider says so: a GPT-5.5/GPT-5.4 request over 272K input tokens
and a Gemini Pro request over 200K prompt tokens use the long-context rate; Codex turns run in fast
mode (`service_tier: priority`) and OpenCode's `-fast` models use OpenAI's fast-tier rates; DeepSeek
V4 on OpenCode Go uses its peak rate for requests made 01:00–04:00 or 06:00–10:00 UTC on weekdays.
Claude 5-minute and 1-hour cache writes use different rates. The estimate excludes plan allowances,
Batch pricing, server-side tool fees, taxes, and discounts. A model without an exact verified rate,
or a record whose tier has no published rate (fast-mode long context, Gemini 3 Flash Preview with
cached input), is marked *unpriced*; its tokens remain in usage totals but no zero cost is implied.
Models with no official rate at all — for example Poolside Laguna, Gemini 3 Pro Preview (no longer
listed), custom OpenCode providers, and Codex records before a model is known — stay unpriced.
Current rates are applied to old records, not historical price schedules; changing rates requires a
new catalog snapshot. Published Go cache-write rates are included for MiniMax M2.7/M2.5 and Qwen3.8
Max/Flash and Qwen3.7 Max; a Go cache-write record is unpriced when Go publishes no cache-write rate.

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

The current Codex and Claude Code profiles appear automatically, as do secondary profiles:
a `.codex-<name>` or `.claude-<name>` folder (also with `_`) directly under your home folder or a
running WSL distribution's home counts when it holds that agent's sign-in (`auth.json` or a
`sessions` folder for Codex, `.credentials.json` or a `projects` folder for Claude Code). For
example, with `CODEX_HOME="$HOME/.codex-work" codex login` already done, a **Profile · work**
card appears with no further setup. To add another, open **Live quota
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
AgentNotify entry, not its agent profile or sign-in. Every account in **Manage accounts** — the
current Codex and Claude Code profiles, the ones discovered in WSL, and those you added — has an
editable name and profile directory and can be removed. A current or discovered account that is
removed is listed under **Removed accounts** with **Restore**, since it would otherwise be found
again; giving one a different directory turns it into an added account for that directory and moves
the detected entry to **Removed accounts**. The owner-only config stores the names, directories, and
removed IDs; the agent login is unchanged. Up to 16 additional profiles can be listed.

### OpenCode Go local estimate

OpenCode Go publishes [per-model dollar caps and token rates](https://opencode.ai/docs/go/):
the five-hour cap is 20% and the weekly cap is 50% of that model's monthly cap. The page compares
only usage-bearing OpenCode Go records in this machine's SQLite database against those caps for
rolling last-five-hour and last-seven-day periods and a monthly period. Set **Plan renews on day** in
the OpenCode Go section to the day of the month your Go plan renews: the monthly bar then counts
this billing cycle (from local midnight on the latest renewal, with a day past the end of a short
month falling on its last day) and shows when it resets. Without it the monthly bar counts the last
30 days, which does not match the cycle the cap applies to. OpenCode's documentation does not say
whether the 5-hour and weekly limits roll or reset at fixed times, so those stay rolling. It currently has exact verified
rates and caps for 22 model IDs, including Muse Spark 1.2/1.3 Contributor, GLM-5.3, and DeepSeek V4
Pro/Flash (priced at peak or off-peak by request time). Context-tiered and unverified models stay unknown. A window with an unpriced
record has no percentage. This is **not** the Go account's live
remaining quota: usage in other clients or on other machines, multiple Go keys in the same local
database, the actual monthly billing boundary, and provider-side adjustments are unavailable from
the local records. It displays no invented provider reset time or subscription charge.

### API accounts

Live quota reads the agents' own sign-ins. **API accounts** is for a key you paste in yourself: the
broker calls that provider's official balance or spend endpoint and shows what it returns. Their
cards appear at the top of Live quota once an account exists; **Manage API accounts** stays at the
bottom. Each card leads with one balance — DeepSeek's total, Kimi's available balance, SiliconFlow's
total, OpenRouter's remaining limit — formatted in its currency (`$0.56`); the breakdown (granted,
topped-up, voucher, cash) is under **Account details**. Up to 16 accounts are stored.

| Provider | Key type | What it shows |
| --- | --- | --- |
| DeepSeek (`api.deepseek.com`) | API key | Balance per currency (total, granted, topped-up) and availability. No usage history exists. |
| Moonshot AI Kimi, international platform (`api.moonshot.ai`) | API key | Balance in USD (available, voucher, cash). |
| SiliconFlow (`api.siliconflow.com`) | API key | Balance amounts with no currency (the provider does not document one). |
| OpenRouter (`openrouter.ai`) | API key | Spend today, this week, this month, all-time, and remaining limit, in USD credits. |
| OpenAI (`api.openai.com`) | Admin key | Daily spend for 30 days, month-to-date and 30-day totals. |
| Anthropic (`api.anthropic.com`) | Admin key | Daily spend for 30 days, month-to-date and 30-day totals. |

The add form warns before the key field: the key is stored encrypted for this user (the page
shows the broker's secret protection), is sent only to that provider's host, anyone who can run
programs as this user could decrypt it, prefer a dedicated key with the least access, and for
OpenAI and Anthropic an Admin key can manage the whole organization. Adding requires ticking
**I understand**. Keys are write-only: they are never displayed, listed, or logged.

Meta (Muse Spark), Z.ai, Xiaomi MiMo, Google Gemini, and xAI are not listed: none of them
offers a usable official balance or spend endpoint for a normal key, so there is nothing honest
to show.

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
