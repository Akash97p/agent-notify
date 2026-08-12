# Project: AgentNotify — Local Notification Broker for Coding Agents on Windows 11

You are an autonomous coding agent running **inside WSL on a Windows 11 machine**.

Your task is to design and implement a Windows-native desktop application called **AgentNotify**.

Do not stop after planning. Work through the project end-to-end in one continuous execution unless you encounter a genuine blocker that cannot be solved locally.

Before writing implementation code, you MUST create project documentation files that preserve the requirements, architecture, implementation plan, and TODO state.

---

# 1. Environment

You are running inside **WSL**, but the application being developed is a **Windows 11 native desktop application**.

Important environment assumptions:

- The repository should preferably live on the Windows filesystem, for example:

  `C:\dev\AgentNotify`

- From WSL this will normally appear as:

  `/mnt/c/dev/AgentNotify`

- You may edit source code from WSL.

- You may invoke Windows executables from WSL.

Examples:

```bash
dotnet.exe --info
dotnet.exe build
dotnet.exe test
powershell.exe -NoProfile -Command "..."
```

- Do NOT attempt to build WPF using Linux `dotnet`.
- Use the Windows .NET SDK via `dotnet.exe`.

The intended development environment is:

- Windows 11
- WSL
- OpenCode running inside WSL
- Windows .NET SDK
- .NET 10
- WPF
- ASP.NET Core Minimal API

Before implementation, inspect the environment and determine:

- repository location
- availability of `dotnet.exe`
- installed .NET SDK versions
- whether .NET 10 SDK is installed
- whether Windows desktop/WPF build tooling is available

Document any missing prerequisites clearly.

Do not install large dependencies automatically unless absolutely required.

---

# 2. Project Goal

Build a small background Windows application that acts as a **human-attention broker for coding agents**.

I regularly run multiple coding agents simultaneously.

They may be:

- in different terminals
- in different Windows Terminal tabs
- in different windows
- on different virtual desktops
- working on different repositories
- waiting for input while I am focused elsewhere

Agents need a reliable way to notify me when:

- they need user input
- they need approval
- they require a decision
- they encounter an error
- they finish a task
- they reach an important milestone
- they are blocked
- they need me to switch back to them

AgentNotify should run in the background on Windows and expose a simple local interface agents can call.

The main notification mechanism should be a **dedicated custom toast UI owned by AgentNotify**, not just the standard Windows notification system.

---

# 3. Product Philosophy

This is not merely a generic toast utility.

Treat it as a:

> Human attention broker for autonomous coding agents.

The system should understand concepts such as:

- agent identity
- project identity
- agent instance
- attention required
- unresolved requests
- completed work
- errors
- urgency
- notification lifecycle
- history

The application should eventually make it easy to answer:

> Which agents are waiting for me right now?

V1 should establish a clean architecture that can evolve toward that goal.

---

# 4. Required Technology Stack

Use the following unless there is a compelling technical reason not to.

## Language

C#

## Runtime

.NET 10

## Desktop UI

WPF

Do NOT use:

- Electron
- Node.js for the desktop app
- Python GUI frameworks
- WinUI 3 for V1
- MAUI
- Avalonia
- webview-based UI

WPF is preferred because custom desktop toast windows, positioning, multiple monitors, stacking, animation, topmost behavior, and tray integration are straightforward.

## Local API

ASP.NET Core Minimal API hosted inside the same application process.

Use Kestrel.

Bind only to loopback:

`127.0.0.1`

Do not bind to:

`0.0.0.0`

Default port can be:

`47821`

Make the port configurable.

## Serialization

`System.Text.Json`

## System Tray

Use an appropriate Windows tray implementation.

Using WinForms `NotifyIcon` from the WPF process is acceptable.

## Persistence

For the first implementation, prefer a simple design.

Either:

- SQLite

or

- a clean persistence abstraction with JSON/in-memory implementation initially

If SQLite adds little complexity, use SQLite.

The architecture should not tightly couple UI logic to persistence.

## Testing

Use a standard .NET test framework.

Prefer:

- xUnit

Add tests for business logic and API behavior where practical.

---

# 5. Explicit Non-Goals for V1

Do NOT unnecessarily introduce:

- Windows Service architecture
- Session 0 service
- MSIX packaging
- Windows App SDK
- native Windows Notification Center integration
- named pipes
- MCP server
- cloud backend
- external authentication provider
- Docker
- browser frontend
- SQL Server
- Redis
- message broker
- telemetry service
- account/login system

These can be future enhancements.

The V1 should remain small and understandable.

---

# 6. Process Model

AgentNotify should run as a **per-user Windows desktop background application**.

It must NOT run as a Windows Service.

It should:

1. start when the user logs into Windows
2. run in the interactive desktop session
3. show a system tray icon
4. host a localhost HTTP API
5. show custom toast windows
6. maintain notification state/history
7. optionally open a notification center/dashboard

The app should continue running when all normal windows are closed.

Exiting should happen explicitly from the tray menu.

---

# 7. Main Architecture

Use a clean separation similar to:

```text
Coding Agent
     |
     | CLI / REST
     v
AgentNotify
     |
     +-- Local HTTP API
     |
     +-- Notification Router
     |
     +-- Notification State Store
     |
     +-- Custom Toast Manager
     |
     +-- Tray UI
     |
     +-- Notification Center
```

Suggested solution organization:

```text
AgentNotify/
|
+-- src/
|   |
|   +-- AgentNotify.App/
|   |
|   +-- AgentNotify.Cli/
|   |
|   +-- AgentNotify.Contracts/
|
+-- tests/
|   |
|   +-- AgentNotify.Tests/
|
+-- docs/
|
+-- README.md
+-- REQUIREMENTS.md
+-- STACK.md
+-- PLAN.md
+-- TODO.md
```

You may improve this structure if appropriate.

Avoid unnecessary layering and enterprise architecture.

---

# 8. Mandatory Documentation-First Workflow

Before writing application implementation code, create the following files.

## REQUIREMENTS.md

Record all functional and non-functional requirements.

Include:

- project purpose
- user scenario
- notification types
- API requirements
- UI requirements
- lifecycle behavior
- security requirements
- startup behavior
- WSL development constraints
- MVP scope
- explicit non-goals
- future ideas

## STACK.md

Record the chosen stack and why.

Include:

- C#
- .NET 10
- WPF
- ASP.NET Core Minimal API
- Kestrel
- System.Text.Json
- tray implementation
- persistence choice
- test framework
- Windows/WSL interoperability
- build commands

Also document rejected alternatives where relevant.

## PLAN.md

Create an implementation plan broken into logical phases.

For example:

1. environment validation
2. solution scaffolding
3. contracts
4. notification domain model
5. local API
6. custom toast UI
7. tray integration
8. dashboard/history
9. CLI
10. authentication
11. startup
12. tests
13. documentation
14. manual verification

The actual plan may be improved.

## TODO.md

Create a checkbox-based task list.

Example:

```markdown
# TODO

## Foundation

- [ ] Validate Windows .NET SDK
- [ ] Create solution
- [ ] Create projects

## API

- [ ] Implement notification endpoint
- [ ] Implement health endpoint
...
```

IMPORTANT:

As work progresses, update `TODO.md`.

Mark completed tasks with:

`[x]`

Do not leave TODO.md as a static initial document.

By the end, TODO.md should accurately reflect what was completed and what remains.

---

# 9. Notification Types

Support at least the following semantic notification types:

- `info`
- `success`
- `warning`
- `error`
- `input_required`
- `permission_required`
- `completed`
- `blocked`

Represent these cleanly, preferably with an enum internally while keeping the JSON API ergonomic.

Also support urgency or priority.

Suggested values:

- `low`
- `normal`
- `high`
- `critical`

Avoid over-engineering.

---

# 10. Notification API

Expose a local REST API.

Base address:

```text
http://127.0.0.1:47821
```

Version API routes from the start.

For example:

```text
/v1/...
```

At minimum implement:

## Health

```http
GET /health
```

or:

```http
GET /v1/health
```

Response should indicate the broker is running.

## Create notification

```http
POST /v1/notifications
```

Example request:

```json
{
  "agent": "opencode",
  "agentInstance": "agent-3",
  "project": "AgentNotify",
  "type": "input_required",
  "title": "Need your decision",
  "message": "Should I use SQLite for notification history?",
  "priority": "high",
  "cwd": "C:\\dev\\AgentNotify"
}
```

Return a notification ID.

Example conceptual response:

```json
{
  "id": "..."
}
```

## List notifications

```http
GET /v1/notifications
```

Support reasonable filtering if simple.

Possible filters:

- unresolved
- type
- project
- agent

Do not overbuild query functionality.

## Get one notification

```http
GET /v1/notifications/{id}
```

## Update notification

Support changing state.

For example:

```http
PATCH /v1/notifications/{id}
```

Possible request:

```json
{
  "status": "resolved"
}
```

## Dismiss notification

Either:

```http
POST /v1/notifications/{id}/dismiss
```

or use the PATCH endpoint.

Pick a clean approach.

---

# 11. Notification Data Model

A notification should support fields similar to:

```text
id
agent
agentInstance
project
type
priority
title
message
cwd
createdAt
updatedAt
resolvedAt
status
metadata
```

Possible statuses:

- active
- dismissed
- resolved

Do not make the model unnecessarily complicated.

Allow an optional extensible metadata object.

---

# 12. Custom Toast UI

This is a core feature.

Implement dedicated WPF toast windows.

Approximate appearance:

```text
+--------------------------------------+
| ● OpenCode                 AgentNotify|
|                                      |
| Need your input                      |
| Should I modify the database schema? |
|                                      |
| [ Open Agent ]            [ Dismiss ]|
+--------------------------------------+
```

Requirements:

- borderless
- visually clean
- compact
- positioned near the edge of the screen
- default position: bottom-right or top-right
- stack multiple notifications
- account for Windows taskbar/work area
- support multiple monitors sensibly
- do not appear partially off-screen
- avoid stealing keyboard focus
- remain above normal windows when appropriate
- allow dismissal
- support different visual treatment by type/priority
- animation is optional but desirable if straightforward

The application must manage multiple simultaneous toast windows.

Avoid each toast independently choosing its own coordinates.

Create a centralized toast positioning/stack manager.

---

# 13. Toast Lifetime Behavior

Different notification types should behave differently.

Suggested defaults:

## completed / success

Auto-dismiss after around 5 seconds.

## info

Auto-dismiss after around 5–8 seconds.

## warning

Stay somewhat longer.

## error

Stay longer or until dismissed.

## input_required

Remain visible until dismissed or resolved.

## permission_required

Remain visible until dismissed or resolved.

## blocked

Remain visible until dismissed or resolved.

Make durations configurable rather than scattering magic constants.

If an API notification is later marked resolved, its active toast should disappear automatically.

---

# 14. Do Not Steal Focus

This requirement is important.

A toast appearing must not unexpectedly steal keyboard focus while I am typing in another terminal/editor/application.

Research and implement the appropriate WPF/Win32 behavior.

Be careful with:

- activation
- topmost
- focus
- click interaction

The user must still be able to click the toast intentionally.

---

# 15. Notification Center / Dashboard

Clicking the tray icon should open a compact notification center.

At minimum show:

## Needs attention

Notifications such as:

- input_required
- permission_required
- blocked
- unresolved error

## Recent

Recently completed/dismissed/resolved notifications.

Conceptual UI:

```text
AgentNotify

NEEDS ATTENTION

OpenCode — backend
Needs schema decision
2 minutes ago
[Open] [Dismiss]

Codex — frontend
Permission required
7 minutes ago
[Open] [Dismiss]

RECENT

Claude — auth
Task complete
9 minutes ago
```

It does not need to be visually elaborate.

Prioritize clarity and useful information.

---

# 16. Tray Icon

Provide a system tray icon.

Left-click may open the notification center.

Right-click menu should contain useful items such as:

```text
AgentNotify
----------------
Open Notification Center
Pause Notifications
Do Not Disturb
Settings
----------------
Exit
```

It is acceptable for some advanced menu items to be stubbed or omitted from the first functional version, but core tray behavior must work.

The application should keep running if the notification center window is closed.

---

# 17. CLI Tool

Create a small CLI called conceptually:

```text
agentnotify
```

The CLI exists so coding agents do not need to construct HTTP manually.

Examples:

```powershell
agentnotify send --type completed --title "Task complete" --message "Tests passed."
```

```powershell
agentnotify send ^
  --agent opencode ^
  --project backend ^
  --type input-required ^
  --title "Need input" ^
  --message "Should I remove the old migration?"
```

Also provide a simple shorthand if practical:

```powershell
agentnotify "Build completed"
```

The CLI should call the local REST API.

Do not duplicate notification business logic in the CLI.

The broker is the source of truth.

---

# 18. WSL CLI Usability

Because agents frequently run inside WSL, make AgentNotify convenient to call from WSL.

For example, agents should eventually be able to execute something like:

```bash
agentnotify.exe send \
  --agent opencode \
  --project AgentNotify \
  --type input-required \
  --title "Need input" \
  --message "Choose option A or B"
```

Consider paths carefully.

Provide documentation for calling the Windows CLI from WSL.

If useful, also provide a tiny WSL shell wrapper such as:

```bash
agentnotify
```

that invokes the Windows executable.

Do not make WSL integration unnecessarily complicated.

---

# 19. Local Authentication

Even though the API listens only on localhost, add lightweight authentication.

On first launch:

- generate a cryptographically random local token
- store it in a per-user AgentNotify configuration directory

Suggested location:

```text
%LOCALAPPDATA%\AgentNotify\
```

For example:

```text
%LOCALAPPDATA%\AgentNotify\config.json
```

or a dedicated secret file.

The CLI should automatically obtain/use the local secret where possible.

HTTP requests should support something similar to:

```http
Authorization: Bearer <token>
```

Do not hardcode the secret.

Do not commit it to git.

Document how WSL-based agents can authenticate.

Keep this simple.

This is not internet-facing security.

---

# 20. API Safety

Implement basic protections:

- loopback binding only
- authentication
- reasonable request size limits
- validate required fields
- reject absurdly large notification payloads
- sensible title/message length limits
- basic rate limiting if straightforward
- graceful malformed JSON handling

Do not create an enterprise security framework.

---

# 21. Configuration

Create a simple configuration model.

Possible configurable values:

```text
port
toast location
toast duration
maximum visible toasts
history retention
launch at startup
sounds enabled
pause notifications
```

Not all require a UI in V1.

Configuration may initially live in:

```text
%LOCALAPPDATA%\AgentNotify\config.json
```

Use sensible defaults.

---

# 22. Persistence

Persist enough state so that restarting AgentNotify does not necessarily lose all useful history.

At minimum preserve recent notification history.

If using SQLite, create a small schema with fields such as:

```text
notifications
-------------
id
agent
agent_instance
project
type
priority
title
message
cwd
created_at
updated_at
resolved_at
status
metadata_json
```

Keep persistence behind an interface such as:

```text
INotificationRepository
```

Do not write SQL directly from WPF UI code.

---

# 23. Duplicate / Spam Handling

Multiple coding agents can become noisy.

Implement basic deduplication or replacement behavior if straightforward.

For example, optionally allow requests to specify a logical key:

```json
{
  "key": "agent-3-awaiting-user"
}
```

If another active notification arrives with the same logical key, update or replace the existing notification rather than creating unlimited duplicates.

This is useful but should not derail the MVP.

If it materially complicates V1, document it as a follow-up item.

---

# 24. Project and Agent Context

Source context should be treated as first-class information.

Support:

- agent name
- agent instance ID
- project
- working directory
- optional process ID
- optional custom metadata

Example:

```json
{
  "agent": "opencode",
  "agentInstance": "oc-71dc",
  "project": "payments",
  "cwd": "C:\\dev\\payments",
  "pid": 18232,
  "type": "input_required",
  "title": "OpenCode needs input",
  "message": "There are two possible schema designs.",
  "priority": "high"
}
```

---

# 25. Open Agent Capability

Design the data model/API so that notifications can eventually contain information required to return to the relevant agent.

Possible concepts:

- process ID
- terminal process
- working directory
- custom launch URI
- terminal profile
- window handle
- command

For V1:

Do NOT spend excessive effort implementing cross-virtual-desktop window activation unless it is straightforward.

However:

- include an `Open Agent` action in the architecture
- implement any safe/basic version that is practical
- document limitations
- do not fake functionality that does not work reliably

---

# 26. Startup at Login

The application should support starting automatically when the Windows user logs in.

Use a per-user startup mechanism.

Do NOT use a Windows Service.

Implement startup registration if reasonably simple.

At minimum create a clean abstraction and document manual startup setup.

Prefer a standard Windows per-user approach.

---

# 27. Single Instance

Only one AgentNotify broker should run per Windows user.

Implement single-instance behavior.

If a second instance launches:

- detect the existing instance
- avoid starting another API server
- ideally bring the existing notification center forward or simply exit cleanly

Use a standard Windows mechanism such as a named mutex.

This use of a mutex is separate from using named pipes for API communication.

---

# 28. Logging

Add lightweight local logging.

Log useful events such as:

- app startup
- API startup
- notification received
- notification displayed
- notification resolved
- major exceptions
- persistence failures

Avoid verbose logs containing secrets.

Store logs somewhere under:

```text
%LOCALAPPDATA%\AgentNotify\
```

A simple logging provider is sufficient.

---

# 29. Graceful Error Handling

The app should not crash because:

- malformed API payload arrives
- one notification fails to render
- persistence temporarily fails
- tray icon operation fails
- malformed config exists

Use sensible exception handling.

Do not swallow every exception silently.

---

# 30. README

Create a polished `README.md`.

Include:

- what AgentNotify is
- problem it solves
- screenshots placeholder if no screenshots are available
- architecture overview
- requirements
- Windows development setup
- WSL development workflow
- build instructions
- run instructions
- API examples
- CLI examples
- configuration
- startup behavior
- testing
- current limitations
- roadmap

Include copy/paste commands.

---

# 31. Build Commands

Ensure the project can be built from WSL using Windows tooling.

For example:

```bash
cd /mnt/c/dev/AgentNotify

dotnet.exe restore
dotnet.exe build
dotnet.exe test
```

If WSL causes path or shell quoting problems, solve and document them.

Do not rely on opening Visual Studio manually.

Visual Studio may be installed for SDK/toolchain components, but the entire project should be operable from the command line.

---

# 32. Development Style

Use modern C# practices.

Prefer:

- nullable reference types
- async/await where appropriate
- dependency injection
- concise classes
- clear interfaces at external boundaries
- cancellation tokens for long-running operations
- strongly typed options/configuration
- immutable request DTOs where practical

Avoid:

- giant service locator
- static global mutable state
- unnecessary reflection
- overuse of abstract factories
- generic repository frameworks
- CQRS/event sourcing
- MediatR unless genuinely justified
- excessive interfaces for trivial classes

This is a small desktop utility.

Keep it understandable.

---

# 33. UI Style

Use a clean, restrained native-looking interface.

Do not spend the majority of implementation effort on styling.

Desired characteristics:

- dark mode compatibility if straightforward
- readable typography
- clear hierarchy
- compact
- subtle borders/shadows
- no giant title bars
- no web-dashboard appearance
- toast should feel native and lightweight

Functionality first.

---

# 34. Agent Integration Documentation

Create documentation showing coding agents how to use AgentNotify.

For example create:

```text
docs/AGENT_INTEGRATION.md
```

Include recommendations such as:

When an agent needs user input:

```bash
agentnotify.exe send \
  --type input-required \
  --title "Need input" \
  --message "Should I proceed with migration A?"
```

When finished:

```bash
agentnotify.exe send \
  --type completed \
  --title "Task complete" \
  --message "Implementation complete and tests pass."
```

When blocked:

```bash
agentnotify.exe send \
  --type blocked \
  --title "Blocked" \
  --message "Required SDK is missing."
```

Also include a short agent-instruction snippet that can be pasted into coding-agent system prompts.

---

# 35. API Contract Documentation

Create documentation such as:

```text
docs/API.md
```

Document:

- base URL
- authentication
- endpoints
- request schemas
- response schemas
- notification types
- priorities
- statuses
- examples using:
  - curl
  - PowerShell
  - WSL

---

# 36. Testing Requirements

Add tests for important non-visual functionality.

At minimum test where practical:

- notification validation
- lifecycle transitions
- notification repository
- API authentication
- API create notification
- API update/resolve behavior
- deduplication if implemented
- configuration parsing

Do not attempt brittle pixel-level WPF UI tests.

Manual UI verification is acceptable for toast placement and focus behavior.

---

# 37. Manual Verification

After implementation:

1. build the complete solution
2. run tests
3. launch the Windows application
4. verify the tray icon appears
5. verify the API responds
6. send a test notification
7. send multiple notifications
8. confirm toasts stack correctly
9. confirm toast does not steal keyboard focus
10. test input_required persistence
11. resolve it through the API
12. verify the toast disappears
13. open notification center
14. verify history
15. verify CLI
16. test CLI invocation from WSL if possible
17. test malformed API payload
18. test unauthorized API call
19. test second app instance behavior

Record results in documentation.

Create:

```text
VERIFICATION.md
```

or:

```text
docs/VERIFICATION.md
```

Clearly distinguish:

- verified
- not verified
- blocked by environment

Do not claim something was tested if it was not.

---

# 38. Git Hygiene

Create or update `.gitignore`.

Do not commit:

- build output
- `.vs`
- local secrets
- generated authentication token
- local database if it contains runtime state
- logs
- machine-specific config

Keep project-generated configuration templates if useful.

---

# 39. Execution Rules

This is important.

You are expected to execute this task autonomously.

Follow this order:

## Phase A — Inspect

Inspect:

- current directory
- repository state
- Windows filesystem path
- `dotnet.exe`
- SDK versions
- existing files

Do not destroy unrelated existing work.

## Phase B — Write project documentation

Create first:

```text
REQUIREMENTS.md
STACK.md
PLAN.md
TODO.md
```

These must exist BEFORE substantial implementation work begins.

## Phase C — Scaffold

Create solution and projects.

## Phase D — Implement

Implement the MVP incrementally.

After meaningful milestones:

- build
- fix compile errors
- update TODO.md

Do not wait until the end to compile everything.

## Phase E — Test

Run automated tests.

Fix issues.

## Phase F — Run / verify

Perform as much Windows-side runtime verification as the environment permits.

## Phase G — Documentation

Finish:

```text
README.md
docs/API.md
docs/AGENT_INTEGRATION.md
docs/VERIFICATION.md
```

## Phase H — Final cleanup

- run formatter if appropriate
- build
- test
- inspect git diff/status
- update TODO.md
- make sure docs match actual implementation

---

# 40. Important Autonomy Instruction

Do NOT stop after:

- creating a plan
- scaffolding
- writing TODOs
- producing an architecture document

Those are preparation steps.

After creating the documentation, immediately continue into implementation.

Do not ask me to confirm the plan unless there is a truly destructive or impossible decision.

Use reasonable engineering judgment.

If a detail is unspecified:

- choose a sensible implementation
- document the choice
- continue

If a feature proves too large for V1:

- implement the cleanest useful subset
- document the limitation
- put the remaining work in TODO.md
- continue with the rest of the project

The goal is to get to a working application in one run.

---

# 41. Avoid Scope Creep

Prioritize in roughly this order:

1. application starts reliably
2. local API works
3. custom toast works
4. toast does not steal focus
5. multiple toasts stack
6. input-required notifications persist
7. notification center works
8. tray works
9. CLI works
10. WSL integration works
11. authentication works
12. persistence works
13. startup works
14. polish

Do not let optional visual polish prevent completion of core functionality.

---

# 42. Expected MVP User Flow

The final result should make this workflow possible.

I am working elsewhere on Windows.

An OpenCode agent running inside WSL executes something equivalent to:

```bash
agentnotify.exe send \
  --agent opencode \
  --project AgentNotify \
  --type input-required \
  --priority high \
  --title "Need your decision" \
  --message "Should I persist dismissed notifications?"
```

AgentNotify receives the HTTP request.

A toast appears on Windows without stealing keyboard focus:

```text
+--------------------------------------+
| OpenCode              AgentNotify    |
| AgentNotify                          |
|                                      |
| Need your decision                   |
| Should I persist dismissed           |
| notifications?                       |
|                                      |
| [ Open Agent ]            [ Dismiss ]|
+--------------------------------------+
```

Because this is `input_required`, it remains visible.

I can dismiss it, open the notification center, or eventually return to the agent.

The agent can later resolve the notification using its ID.

A completed task may instead generate:

```text
+--------------------------------------+
| OpenCode                             |
|                                      |
| Task complete                        |
| Implementation finished. Tests pass. |
+--------------------------------------+
```

which automatically disappears after a few seconds.

---

# 43. Future Architecture — Document but Do Not Implement Unless Trivial

Create a roadmap containing potential future features:

- Windows native Notification Center integration through Windows App SDK
- MCP server
- named pipe transport
- richer agent SDKs
- terminal/window activation
- virtual desktop awareness
- terminal tab detection
- sounds per agent
- custom agent icons
- notification grouping
- snooze
- quiet hours
- DND schedules
- agent heartbeat/status
- webhooks
- acknowledgement callbacks
- response buttons
- sending responses back to agents
- notification priorities
- notification coalescing
- per-project rules
- remote/LAN support with stronger authentication
- update mechanism
- installer
- MSIX or alternative packaging

These are future ideas and should not complicate V1.

---

# 44. Definition of Done

The task is considered successfully completed when, as far as the available environment permits:

- `REQUIREMENTS.md` exists
- `STACK.md` exists
- `PLAN.md` exists
- `TODO.md` exists and reflects final progress
- solution builds successfully with Windows `dotnet.exe`
- tests pass
- WPF application exists
- application runs as a tray/background app
- localhost API exists
- API is loopback-only
- API has local authentication
- agents can create notifications
- custom WPF toast appears
- multiple toasts stack
- persistent attention-required notifications work
- notifications can be resolved
- notification center exists
- CLI exists
- CLI can send notifications
- WSL usage is documented
- README exists
- API documentation exists
- agent integration documentation exists
- verification results are documented
- repository has sensible git hygiene

If something cannot be achieved because the Windows development environment is missing a prerequisite, do everything else possible and document the exact missing prerequisite and command/output proving the blocker.

Do not silently skip requirements.

---

# 45. Final Response After Work

When you finish, give me a concise implementation report containing:

1. what you built
2. architecture used
3. important files
4. build result
5. test result
6. manual verification result
7. anything not completed
8. any prerequisite I need to install
9. exact command I should run from WSL to launch/build/test
10. exact example command an OpenCode agent can use to notify me

Also inspect `git status` before finishing.

Do not merely tell me what I should implement.

Implement it.
