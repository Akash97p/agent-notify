# Install AgentNotify — instructions for a coding agent

You are a coding agent (Claude Code, Codex, OpenCode, or similar) and the person you work for has asked
you to install **AgentNotify** on this computer, set it up, and prove it works. Follow these steps in
order. Work in the terminal you already have. At the end, show the person the checklist described in
[Report](#6-report) and nothing longer.

AgentNotify is a local notification broker for coding agents: agents tell it when a task finishes, when
they need an answer, or when something failed, and it shows a desktop notification (and, if the person
sets it up, sends it to their phone). It also has a web interface, token usage and live quota views for
agent accounts, and an optional local model router. Project: <https://github.com/Akash97p/agent-notify>.

## Rules

- **Never print or paste the bearer token** (`agentnotify token`, `authToken` in `config.json`) or the
  router key. Commands below that need them read them themselves.
- **Do not change the person's agent configuration beyond what this file says.** Installing the
  AgentNotify skill for yourself is in scope. Installing hook harnesses, connecting agents to the model
  router, turning the router on, pairing a phone Relay, or adding providers is **not** — mention them
  at the end as optional next steps.
- **Ask before anything that needs administrator rights** or that accepts a licence on the person's
  behalf. The default install needs neither.
- If a step fails, do not guess around it. Record the failure, carry on with the checks that can still
  run, and report it as ❌ with the reason.
- The broker listens only on `127.0.0.1`, port `47821` by default. If `agentnotify ui --print` shows a
  different address, use that one everywhere below.

## 1. Find the platform and what is already there

Work out the OS and CPU (`uname -sm` on macOS/Linux; on Windows, `$env:PROCESSOR_ARCHITECTURE` in
PowerShell). If you are inside **WSL**, install the **Windows** build: AgentNotify runs on the Windows
side and WSL reaches it on `127.0.0.1`.

Check for an existing install: `agentnotify --version` (Windows: `agentnotify.exe --version`). If it is
already installed, find the newest release (step 2) and update only if it is newer; otherwise keep it
and go to step 3.

The newest build is a **prerelease**, so GitHub's `releases/latest` link does not find it. Ask the API
instead and take the first entry:

```sh
curl -fsSL "https://api.github.com/repos/Akash97p/agent-notify/releases?per_page=1"
```

## 2. Install

### macOS and Linux

```sh
curl -fsSL https://raw.githubusercontent.com/Akash97p/agent-notify/main/scripts/install.sh | sh
```

It picks the newest release (prereleases included), verifies the SHA-256 checksum, refuses anything it
cannot verify, and installs `agentnotify` (CLI) and `agentnotifyd` (broker) into `~/.local/bin`. A
macOS archive that contains `agentnotify-menubar` installs it too, and the script re-signs every
installed executable. Make sure `~/.local/bin` is on `PATH`
for the checks below (or call the binaries by full path).

If a binary exits immediately with status 137 on macOS, re-sign it and clear the quarantine flag:

```sh
codesign --force --sign - ~/.local/bin/agentnotify ~/.local/bin/agentnotifyd \
  ~/.local/bin/agentnotify-menubar 2>/dev/null || true
xattr -d com.apple.quarantine ~/.local/bin/agentnotify ~/.local/bin/agentnotifyd \
  ~/.local/bin/agentnotify-menubar 2>/dev/null
```

**Start the broker so it survives logouts and reboots.** Linux has no tray; on macOS the broker starts the installed quota-only status item.

macOS — a launchd agent (replace the path if `HOME` differs):

```sh
mkdir -p ~/Library/LaunchAgents
cat > ~/Library/LaunchAgents/dev.agentnotify.broker.plist <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>dev.agentnotify.broker</string>
  <key>ProgramArguments</key>
  <array><string>$HOME/.local/bin/agentnotifyd</string></array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
</dict>
</plist>
PLIST
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/dev.agentnotify.broker.plist 2>/dev/null \
  || launchctl kickstart -k gui/$(id -u)/dev.agentnotify.broker
```

If the plist already existed (an update), just restart it:
`launchctl kickstart -k gui/$(id -u)/dev.agentnotify.broker`.

Linux — a systemd user service:

```sh
mkdir -p ~/.config/systemd/user
cat > ~/.config/systemd/user/agentnotify.service <<UNIT
[Unit]
Description=AgentNotify broker

[Service]
ExecStart=%h/.local/bin/agentnotifyd
Restart=on-failure

[Install]
WantedBy=default.target
UNIT
systemctl --user daemon-reload
systemctl --user enable --now agentnotify.service
```

On an update, `systemctl --user restart agentnotify.service`. Without systemd, run
`nohup agentnotifyd >/dev/null 2>&1 &` and say in the report that it will not survive a reboot.

### Windows

Download `AgentNotifySetup.exe` from the newest release's assets (step 1) and run it. It installs per
user under `%LOCALAPPDATA%\Programs\AgentNotify` with no administrator rights, adds the CLI to the
user `PATH`, starts the tray app, and starts it at sign-in. **Run it normally so the person sees and
accepts the licence.** Use the silent mode (`AgentNotifySetup.exe --silent --accept-license`) only if
the person has told you they accept the MIT licence; an update needs no licence switch
(`--silent` alone). Windows may show a SmartScreen prompt because the installer is not code-signed.

A new `PATH` is only visible to new shells. If `agentnotify.exe` is not found afterwards, call
`"$env:LOCALAPPDATA\Programs\AgentNotify\agentnotify.exe"` directly.

## 3. Check the broker

```sh
agentnotify --version
agentnotify health
```

`health` must print JSON with `"status": "ok"` (and the version, when it can authenticate). If it
cannot connect, the broker is not running: check the service from step 2 (macOS:
`launchctl print gui/$(id -u)/dev.agentnotify.broker`; Linux: `systemctl --user status agentnotify`;
Windows: the tray icon). Its log is under the data directory: `~/Library/Application Support/AgentNotify/logs`
on macOS, `~/.local/share/AgentNotify/logs` on Linux,
`%LOCALAPPDATA%\AgentNotify\logs` on Windows.

Then prove a notification goes through, and clean it up:

```sh
agentnotify send --title "AgentNotify installed" --message "Test from your coding agent" --type success --key agentnotify-install-check
agentnotify list --limit 5
```

The new notification must appear in `list`. Ask the person whether they saw a desktop notification;
if you cannot ask, report it as sent but not confirmed. Resolve it afterwards with
`agentnotify resolve <id>` using the id from `list`.

## 4. Install the skill for yourself

This is what makes you (and future sessions) notify the person without being told each time.

```sh
agentnotify install-skill claude     # if you are Claude Code
agentnotify install-skill codex      # if you are Codex
agentnotify install-skill opencode   # if you are OpenCode
```

For any other agent, pass its skills folder: `agentnotify install-skill claude --path <skills-root>`
installs the same `SKILL.md` there. If the file already exists and differs, the command refuses; say so
rather than adding `--force`. Confirm the file exists afterwards (for example
`~/.claude/skills/agentnotify/SKILL.md`).

## 5. Check the features

Every check below is a read-only call to the local web interface. Its `/ui/api/...` endpoints need no
token on this machine.

```sh
BASE="$(agentnotify ui --print 2>/dev/null | sed 's#/ui/*$##')"; BASE="${BASE:-http://127.0.0.1:47821}"
```

| Feature | How to check | Works when |
| --- | --- | --- |
| Web interface | `curl -s -o /dev/null -w '%{http_code}' "$BASE/ui/"` | `200`; tell the person the address, `$BASE/ui/` |
| Agents detected | `curl -s "$BASE/ui/api/agents"` | `accounts` lists at least one agent account (Codex, Claude Code, …); name them and whether each has the skill and harness installed |
| Token usage | `curl -s "$BASE/ui/api/usage"` | `events` greater than 0, with `sources` naming the agents it read |
| Live quota | `curl -s "$BASE/ui/api/quota"` | at least one entry in `providers` whose `status` is not an error; name each account and its status |
| Model router | `curl -s "$BASE/ui/api/router"` and `curl -s "$BASE/ui/api/router/agents"` | the endpoints answer. Report `enabled` and how many `upstreams` it has. It is **optional and off by default**, so off is not a failure |
| Phone delivery (Relay) | `curl -s "$BASE/ui/api/providers"` | optional; report whether any provider is enabled |

On Windows use `curl.exe` or `Invoke-RestMethod`. A feature with no data (a fresh machine with no agent
history has no usage) is a warning, not a failure: say what is missing.

## 6. Report

Finish with this, and only this, filled in from what you actually observed. Use ✅ for works, ❌ for
does not work (with the reason in a few words), and ⚪ for an optional feature that is simply not set
up.

```text
AgentNotify <version> installed on <OS> and verified.

✅ Broker running        — <service kind>, http://127.0.0.1:47821
✅ CLI                   — agentnotify <version>
✅ Test notification     — sent and listed (desktop banner: seen / not confirmed)
✅ Web interface         — http://127.0.0.1:47821/ui/
✅ Skill installed       — <path>
✅ Agents detected       — <names>
✅ Token usage           — <n> events from <sources>
✅ Live quota            — <accounts and status>
⚪ Model router          — off (optional)
⚪ Phone delivery        — not set up (optional)

Next steps (optional): <only the ones not already done>
```

The optional next steps to choose from: the hook harness for automatic notifications
(`agentnotify install-harness <agent>`) for an agent that lacks it, pairing a phone (Delivery →
Channels in the web interface), and the model router (Model router → Providers). Leave out any that
the checks showed is already set up; if all are, leave the line out.
