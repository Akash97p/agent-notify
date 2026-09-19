# Live quota on Windows and inside WSL

On Windows the tray application owns the same `LiveQuotaService`; there is no separate provider
implementation per platform. Discovery is what differs, because agent profiles can live in the
Windows user profile, in a WSL distribution's home, or in a hand-added directory.

## Discovery

`WslDiscovery` in Core lists distributions from
`HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss`, asks `wsl.exe --list --running` which of them
are running, and resolves each distribution's default user through that distribution's
`/etc/wsl.conf` (`[user] default=`) before falling back to the registry's `DefaultUid`. Only running
distributions are touched, because opening a stopped distribution's share boots its VM.

Discovery is cached briefly and re-resolved per scan, so a distribution that starts while the broker
runs becomes visible without a restart. Each discovered home contributes its own Codex and Claude
profile entries, and every account can be renamed, moved, removed, and restored from the web
interface.

## Probing through the share

A profile inside a distribution is read through `\\wsl.localhost\<distribution>` using the paths the
agents use inside Linux, because their environment variables are not visible to a Windows broker. A
Codex child process is started with the selected `CODEX_HOME`; a Claude probe reads that profile's
`.credentials.json` when it exists. No credential is copied into the Windows profile, and the
owner-only configuration file stores profile IDs, editable labels, and paths only.

## Boundaries

RPC and endpoint shapes remain provider implementation details and may change without notice.
Every probe stays behind the same versioned fetcher and fixture tests as the native cases, and a
source that fails is reported as unavailable rather than replaced with an estimate. OpenCode Go is
the one exception by design: it is a local published-cap estimate, and it is labelled as an estimate
in every surface that shows it.
