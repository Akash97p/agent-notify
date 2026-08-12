# AgentNotify — technology stack

## Chosen stack

| Concern | Choice | Reason |
|---|---|---|
| Language | C# with nullable references | Native fit for the Windows/.NET runtime and clear contracts |
| Runtime | .NET 10 | Required toolchain; self-contained publishing removes end-user runtime setup |
| Desktop UI | WPF | Precise borderless non-activating windows, animation, positioning, and native desktop lifetime |
| Tray | WinForms `NotifyIcon` inside WPF | Mature, dependable system-tray integration |
| Local API | ASP.NET Core Minimal API + Kestrel in the tray process | Small HTTP surface usable from Windows and WSL without another daemon |
| Serialization | `System.Text.Json` | Built in; shared enum/property policy across API and CLI |
| Persistence | SQLite via `Microsoft.Data.Sqlite` | Durable zero-administration history behind a small repository interface |
| Authentication | Random local bearer token | Lightweight defense for a loopback-only per-user API |
| Logging | Small daily file logger | No extra logging package; errors remain locally diagnosable |
| CLI | .NET console executable | Reuses contracts/config and publishes as a self-contained `agentnotify.exe` |
| Installer | WPF self-extracting per-user setup | One distributable binary without MSIX/administrator/service complexity |
| Tests | xUnit | Standard .NET unit/integration framework |
| License | MIT | Permissive reuse/modification/distribution with an explicit no-warranty clause |

## Projects

```text
AgentNotify.Contracts  net10.0          DTOs, enums, JSON settings
AgentNotify.Core       net10.0          domain, config, SQLite, services, logging
AgentNotify.Api        net10.0          Kestrel endpoints, authentication, rate limit
AgentNotify.App        net10.0-windows  WPF tray process and custom toast UI
AgentNotify.Cli        net10.0          Windows/WSL command-line client
AgentNotify.Setup      net10.0-windows  single-file per-user installer
AgentNotify.Tests      net10.0          xUnit tests
```

The main dependency direction is Contracts ← Core ← API/App/CLI. UI code does not issue SQL, and the CLI never duplicates broker lifecycle rules.

## Key packages

- `Microsoft.Data.Sqlite` 10.0.10 and `SQLitePCLRaw.lib.e_sqlite3` 2.1.12.
- `Microsoft.AspNetCore.App` framework reference.
- xUnit, the Visual Studio test adapter, `Microsoft.NET.Test.Sdk`, and coverlet collector.

The desktop and setup UI otherwise use framework components. There is no Electron, Node desktop runtime, webview, MAUI, Avalonia, WinUI 3, service, or cloud dependency.

## Windows/WSL build toolchain

The repository is in WSL, but WPF compilation and tests use the Windows .NET SDK:

```text
/mnt/d/dev/dotnet/dotnet.exe
.NET SDK 10.0.302
Microsoft.WindowsDesktop.App 10.0.10
```

```bash
cd /path/to/agent-notify
./scripts/build.sh
./scripts/test.sh
./scripts/package.sh
```

Set `AGENTNOTIFY_DOTNET_EXE` to a different Windows .NET 10 executable when required. Linux `dotnet` must not build the WPF projects.

## Packaging choices

`scripts/package.sh` publishes `win-x64`, self-contained, compressed single-file binaries. The installer embeds:

- `AgentNotify.Tray.exe`;
- `agentnotify.exe`;
- `SKILL.md`;
- the offline HTML onboarding template; and
- the MIT License.

The two installed executables cannot differ only by capitalization because Windows paths are case-insensitive; the `.Tray` suffix deliberately avoids that collision.

## Rejected alternatives

| Alternative | Reason rejected for V1 |
|---|---|
| Electron/webview desktop app | Larger runtime and weaker native-window control |
| WinUI 3 / Windows App SDK | More deployment complexity than this per-user utility needs |
| MAUI/Avalonia | Cross-platform abstraction is unnecessary for a Windows-native requirement |
| Windows Service | Cannot own normal interactive per-user UI and tray behavior cleanly |
| MSIX | Not needed for the requested portable single-executable setup |
| Named pipes | Loopback HTTP is easier for diverse WSL/Windows agents in V1 |
| EF Core | Direct, parameterized SQLite access is clearer for one table |
| External broker/database | Redis, SQL Server, queues, and cloud services would be scope without value |
| Inno Setup/NSIS dependency | The self-contained WPF setup avoids installing an additional packaging tool |

## Future stack boundaries

External email, WhatsApp, Teams, Slack, Discord, SMS, push, or webhook adapters must sit behind an asynchronous delivery boundary after local persistence. Provider secrets must use Windows-protected storage rather than the current config token field, and local-only operation must remain the default.
