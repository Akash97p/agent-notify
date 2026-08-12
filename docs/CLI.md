# CLI reference

The `agentnotify` command-line client talks to the local AgentNotify broker over HTTP. Every command except `health` (unauthenticated probe fallback) and `token` requires the broker to be running and a valid bearer token.

Binary name:

- `agentnotify.exe` on Windows (`%LOCALAPPDATA%\Programs\AgentNotify\agentnotify.exe` after installation, added to the current user's `PATH`).
- Reachable from WSL as `agentnotify.exe`. After installation open a new WSL shell so the updated Windows `PATH` is imported.

Source: `src/AgentNotify.Cli/Program.cs`.

---

## Invocation

```text
agentnotify <command> [options]
agentnotify "Title" ["Message"] [options] shorthand for send
agentnotify --help | -h | help [<command>]
agentnotify --version
```

When no command is given the usage text is printed and the process exits `0`. When the first argument does not match a known command (`send`, `list`, `get`, `resolve`, `dismiss`, `health`, `token`, `help`/`--help`/`-h`, `--version`) it is treated as a positional `send` invocation.

All commands that contact the broker use a 10-second HTTP timeout. Connection failure prints to stderr and exits non-zero (see Exit codes).

---

## Common options

`--port` and `--token` are accepted by every broker command. They override file and environment discovery for a single invocation.

| Flag | Short | Value | Required | Default |
|------|-------|-------|----------|---------|
| `--port` | — | integer | no | `47821` from `%LOCALAPPDATA%\AgentNotify\config.json` `port`, or `AGENTNOTIFY_PORT` when `ConfigStore` is constructed with environment overrides |
| `--token` | — | string | no | `authToken` from the same config file, or `AGENTNOTIFY_TOKEN` (see Authentication) |

Token resolution for broker calls (`src/AgentNotify.Cli/Program.cs`):

1. `--token` when supplied.
2. `AGENTNOTIFY_TOKEN` environment variable when `ConfigStore(applyEnvOverrides: true)` is used.
3. `authToken` from `%LOCALAPPDATA%\AgentNotify\config.json`.

If no token is available a warning is written to stderr but the request is still sent; the broker replies `401`.

Base URL is `http://127.0.0.1:{port}`.

`--help` / `-h` on `send` and `list` prints command help and exits `0`.

---

## Value parsing

### Type identifiers

`--type` values are normalized by `AgentNotify.Contracts.NotificationTypes.Normalize` (`src/AgentNotify.Contracts/NotificationTypes.cs`):

- Trim, replace `-` with `_`, lower-case.
- Map `inputrequired` to `input_required` and `permissionrequired` to `permission_required`.
- Accept only `^[a-z][a-z0-9_]{0,63}$` (1–64 characters, starting with a letter). Otherwise `--type` fails with: `--type must be a valid identifier containing letters, numbers, underscores, or hyphens (maximum 64 characters).`

Built-in IDs: `info`, `success`, `warning`, `error`, `input_required`, `permission_required`, `completed`, `blocked`. Custom IDs use the same rule; hyphen and underscore spellings are equivalent.

### Priority and status enums

Enum flags use `Program.TryParseEnum` (`src/AgentNotify.Cli/Program.cs`):

```text
value.Replace('-', '_').Replace("_","") then Enum.TryParse(ignoreCase:true)
```

That makes `high`, `HIGH`, `high-priority` styles accepted as long as letters match. `status` filtering on `list` uses the same normalization in `src/AgentNotify.Api/ApiHost.cs`.

Valid priorities for `--priority`: `low`, `normal`, `high`, `critical`. On failure: `--priority must be low, normal, high, or critical.`

Valid statuses: `active`, `dismissed`, `resolved` (see `src/AgentNotify.Contracts/NotificationStatus.cs`).

---

## Commands

### `send` — create a notification

Creates a notification or, when `key` matches an active notification, updates it in place.

```text
agentnotify send --title T --message M [options]
agentnotify "Title" ["Message"] [options]
agentnotify --title T --msg M [options]
```

| Flag | Value | Required | Default | Notes |
|------|-------|----------|---------|-------|
| `--title` | text | yes (or first positional) | — | Trimmed, 1–200 characters |
| `--message` / `--msg` | text | yes (or second positional) | — | Trimmed, 1–4000 characters |
| `--type` | identifier | no | `info` | Normalized as above |
| `--priority` | enum | no | type default or `normal` | `low`/`normal`/`high`/`critical` |
| `--agent` | string | no | `AGENTNOTIFY_AGENT` or `cli` | Trimmed, max 100 |
| `--agent-instance` | string | no | — | Trimmed, max 100 |
| `--project` | string | no | — | Trimmed, max 200 |
| `--key` | string | no | — | Trimmed, max 100; triggers deduplication |
| `--cwd` | path | no | `Directory.GetCurrentDirectory()` | Trimmed, max 1024 |
| `--pid` | integer | no | — | Parsed with `long.TryParse`; invalid value becomes absent |
| `--port` | integer | no | `47821` | As above |
| `--token` | string | no | file/env | As above |

Positional shorthand (`src/AgentNotify.Cli/Program.cs`):

- First positional sets `--title` when `--title` was not supplied.
- Second positional sets `--message` when `--message` was not supplied.
- A single positional sets both title and message to the same value.

Other behavior:

- `agent` defaults to `AGENTNOTIFY_AGENT` or `cli`. `cwd` defaults to the current directory (failure to read returns no value).
- Unknown `send` flags produce `unknown option '<flag>' for send. Run 'agentnotify help send'.`

Output: on success the broker returns `201` with a `NotificationDto`; the CLI pretty-prints the JSON with indentation to stdout. On failure it prints `Error {code} {Status}: {error}` to stderr where `{error}` is the broker's `{ "error": "..." }` field or the raw body.

Examples:

```bash
# Explicit flags
agentnotify.exe send --title "Build done" --message "All tests passed" --type success

# Positional shorthand
agentnotify.exe "Need input" "Which branch should I use?" --type input_required --key my-task

# With metadata derived from environment
AGENTNOTIFY_AGENT=opencode agentnotify.exe send --title "Task blocked" --message "Missing SDK" --type blocked --priority high --project myrepo --key build-42
```

### `list` — list notifications

```text
agentnotify list [options]
```

| Flag | Value | Required | Default | Notes |
|------|-------|----------|---------|-------|
| `--unresolved` | `true`/`false` or no value | no | not filtered | Bare `--unresolved` means `true`. With a value, parsed by `bool.TryParse` |
| `--type` | identifier | no | — | Exact normalized type filter |
| `--status` | enum | no | — | `active`/`dismissed`/`resolved` (overridden by `--unresolved true`) |
| `--project` | string | no | — | Exact trimmed match |
| `--agent` | string | no | — | Exact trimmed match |
| `--limit` | integer | no | `20` | Parsed by `int.TryParse`; server clamps `1–500` (default `100` when absent) |
| `--json` | — | no | `false` | When present, output raw JSON instead of table |
| `--port` | integer | no | `47821` | As above |
| `--token` | string | no | file/env | As above |

Unknown flags produce `unknown option '<flag>' for list.`

Output:

- Without `--json`: zero or more lines `"{id} [{type}/{priority}] {status} {title} ({agent})"` or `(no notifications)`.
- With `--json`: pretty-printed JSON array `NotificationDto[]`.
- On HTTP error: `Error {code} {Status}: {error}` to stderr.

Examples:

```bash
agentnotify.exe list --unresolved --limit 20
agentnotify.exe list --type error --status active --json
agentnotify.exe list --project myrepo --agent opencode --limit 50
```

### `get` — fetch one notification by id

```text
agentnotify get <id> [--port N] [--token T]
```

- `<id>` is required and must not start with `-`. Otherwise: `get requires an <id>. Usage: agentnotify get <id>`.
- Only `--port` and `--token` are accepted after the id.

Output: pretty-printed `NotificationDto` on success. On failure `Error {code} {Status}: {error}` to stderr.

Example:

```bash
agentnotify.exe get 9e8a3f2b7c6d4e5f8a1b2c3d4e5f6a7b8
```

### `resolve` — mark a notification resolved

```text
agentnotify resolve <id> [--port N] [--token T]
```

- `<id>` required; same validation as `get`.
- Implemented as `PATCH /v1/notifications/{id}` with `{ "status": "resolved" }` (`src/AgentNotify.Cli/Program.cs`).

Output: pretty-printed updated `NotificationDto` on success.

Example:

```bash
agentnotify.exe resolve 9e8a3f2b7c6d4e5f8a1b2c3d4e5f6a7b8
```

### `dismiss` — dismiss a notification

```text
agentnotify dismiss <id> [--port N] [--token T]
```

- `<id>` required.
- Tries `POST /v1/notifications/{id}/dismiss`; on `404` falls back to `PATCH` with `dismissed` (`src/AgentNotify.Cli/Program.cs`).

Output: pretty-printed updated `NotificationDto` on success.

Example:

```bash
agentnotify.exe dismiss 9e8a3f2b7c6d4e5f8a1b2c3d4e5f6a7b8
```

### `health` — check broker health

```text
agentnotify health [--port N] [--token T]
```

Authentication detection (`src/AgentNotify.Cli/Program.cs`):

- `wantAuth` is true when `--token` is supplied or a token file exists (`ConfigStore(applyEnvOverrides: false).Load().AuthToken` is non-empty).
- When `wantAuth` is true the CLI calls `GET /v1/health` (authenticated). On `401` it falls back to `GET /health` and prints the unauthenticated body.
- When `wantAuth` is false it calls `GET /health` directly.

Output:

- Success: pretty-printed JSON. Authenticated shape is `HealthResponse` (`status`, `version`, `pid`, `uptimeSeconds`, `activeCount`, `apiVersion`, `serverTimeUtc`). Unauthenticated shape is `{ "status": "ok" }`.
- On `HttpRequestException`: `Could not reach AgentNotify at http://127.0.0.1:{port}: {message}` plus `Is the app running? Check the tray icon.` to stderr.

Example:

```bash
agentnotify.exe health
agentnotify.exe health --port 47821 --token "$TOKEN"
```

### `token` — print the local bearer token

```text
agentnotify token
```

Reads `ConfigStore().Load()` (default `%LOCALAPPDATA%\AgentNotify\config.json`). On success prints the token to stdout with no additional formatting. Failures:

- `No token found. Has AgentNotify run at least once? Look at: {ConfigPath}` to stderr.
- Any exception message to stderr.

Example:

```bash
TOKEN="$(agentnotify.exe token)"
curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:47821/v1/health
```

### `help` and `--version`

```text
agentnotify help [send|list|get|resolve|dismiss]
agentnotify --help
agentnotify -h
agentnotify --version
```

- `help <topic>` prints the topic help (`send`, `list`, `get`, `resolve`, `dismiss`). Unknown topic prints the general usage.
- `help` with no topic prints general usage (`PrintUsage`).
- `--version` (`RunVersion`) prints `agentnotify {InformationalVersion}` derived from the CLI assembly.

---

## Exit codes

| Code | Meaning | When |
|------|---------|------|
| `0` | Success | Command completed; also returned when no arguments are given (usage printed) or `--help`/`--version` was requested |
| `1` | General failure | Validation error, unknown option, missing required argument, connection failure, request timeout, or any HTTP error not covered below |
| `2` | Authentication failure | `send` received `401` or `403` (`HandleCreateResponse` maps 401/403 to `2`) |
| `3` | Not found | `get`, `resolve`, or `dismiss`/`PATCH` returned `404` |

Additional failure messages written to stderr:

- `Could not reach AgentNotify: {message}` / `Is the tray app running?` on `HttpRequestException` (top-level catch, `src/AgentNotify.Cli/Program.cs`).
- `AgentNotify did not respond before the request timed out.` on `TaskCanceledException`.
- `Could not reach AgentNotify at {baseUrl}: {message}` / `Is the app running? Check the tray icon.` for `health`.

---

## Environment variables

| Variable | Used by | Effect |
|----------|---------|--------|
| `AGENTNOTIFY_AGENT` | `send` | Default for `--agent` when the flag is absent |
| `AGENTNOTIFY_PORT` | all broker commands | Overrides `port` from `config.json` (`ConfigStore.cs`) |
| `AGENTNOTIFY_TOKEN` | all broker commands | Overrides `authToken` from `config.json` (`ConfigStore.cs`) |

---

## Error payload

Broker errors are JSON `{ "error": "<message>" }`. The CLI extracts the `error` field for the `Error {code} {Status}: {message}` line; otherwise it prints the raw body or `(empty response)`.
