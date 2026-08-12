# Bug log

Defects found in AgentNotify after a capability was considered complete, with what actually caused
them and how each was verified. This is a record for contributors: several of these were only
reachable by running the product rather than by reading it or by unit tests, and the pattern is
worth learning from.

Verification detail for each entry lives in [VERIFICATION.md](VERIFICATION.md).

---

## Settings window closed the application when a saved provider was selected

**Found:** 2026-08-12, by the maintainer configuring a real Telegram bot.
**Fixed in:** `fix/settings-json-null-crash`. **Severity:** high — terminated the broker.

### Symptoms

1. **Test send** delivered a message to Telegram successfully, but the Settings window then showed
   an error mentioning null.
2. Clicking the saved **Telegram** provider in the list closed the whole application, every time.

### Cause

`JsonElement.TryGetInt32` does not behave the way its name implies. It returns `false` only when the
element is a number that will not fit in an `Int32`, and **throws `InvalidOperationException` for
every other value kind, including JSON `null`**.

The Settings window read optional integers as:

```csharp
root.TryGetProperty("messageThreadId", out var thread) && thread.TryGetInt32(out var threadId)
```

Optional provider settings are serialized as `null` when the user leaves the field blank, so a
Telegram provider saved without a topic ID stores `"messageThreadId": null` — and reading it back
threw. Any Telegram provider without a topic ID was affected, which is the common case.

The two symptoms were the same exception surfacing in two different places:

- `Provider_Selected` runs **outside** `RunAsync`, so the exception reached the WPF dispatcher
  unhandled and terminated the tray process, taking the broker and its loopback API down with it.
- `TestProvider_Click` runs **inside** `RunAsync`, which catches everything and displays
  `exception.Message`. It saves, sends, then reloads and reselects the provider — so the throw
  happened *after* Telegram had already accepted the message, making a successful send look failed.

The same unsafe pattern existed at **eight** call sites: SMTP port, Telegram topic ID, Pushover
emergency retry and expiry, Twilio SMS validity, Twilio WhatsApp validity, and MQTT port, QoS, and
message expiry. Telegram was simply the first provider configured with a real account.

### Fix

`AgentNotify.Core.JsonConfigReader` reads optional values and treats absent, `null`, and
wrong-typed entries alike as "not set". All eight sites use it. `Provider_Selected` additionally
catches anything a stored profile can throw and reports it in the status line, so no saved row can
terminate the process again.

### Lessons

- A `Try`-prefixed API is not automatically total. `JsonElement.TryGetInt32` throws for the input a
  user is most likely to produce. A test now asserts that behaviour so the assumption is pinned.
- `catch (JsonException)` around JSON handling looked defensive but caught the wrong exception type.
- Any handler that can run outside the panel's `RunAsync` wrapper can kill the process. UI event
  handlers need their own guard.

---

## Local state could be written into the working directory on macOS and Linux

**Found:** 2026-08-12, by running the Linux broker. **Fixed in:** `feature/cross-platform-core`.
**Severity:** high — wrote the local bearer token to an unexpected location.

On Unix, `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` returns an **empty string**
when the directory does not exist yet, which is the normal state of a fresh account. Combining that
empty string produced the relative path `AgentNotify`, so the first run wrote `config.json` —
containing the local bearer token — plus `secret.key` and the history database into whatever
directory the broker happened to start in. On a developer machine that is the repository being
worked on, where it could be committed.

`ConfigStore.DefaultConfigDir` now resolves through `SpecialFolderOption.Create`, then
`XDG_DATA_HOME`, then `$HOME`, and always returns an absolute path. Covered by a regression test.

---

## `agentnotifyd` ignored SIGTERM

**Found:** 2026-08-12, by stopping the Linux broker. **Fixed in:** `feature/cross-platform-core`.
**Severity:** high — the daemon could not be stopped normally.

The `PosixSignalRegistration` objects were created and discarded. Once finalized, the handler is
unhooked, so the broker neither shut down nor exited on `SIGTERM` and could only be stopped with
`SIGKILL` — unmanageable under systemd or launchd. The registrations are now held for the lifetime
of the process. Shutdown could also hang forever on an unbounded dispatcher stop; every step is now
bounded and a second signal exits immediately.

---

## Sound file names were sanitized inconsistently across platforms

**Found:** 2026-08-12, by the first Linux and macOS CI run. **Fixed in:**
`fix/portable-filename-sanitizing`. **Severity:** medium.

`Path.GetFileName` is platform-dependent: Windows treats `\` as a separator, Unix treats it as an
ordinary file-name character. A configured sound of `C:\outside\global.MP3` therefore passed through
sanitizing unchanged on Linux and macOS, so the invariant that a configured sound is a bare file
name inside the managed directory did not hold there. Since `config.json` is portable between
machines, `SafeFileName.Last` now normalizes identically on every platform using plain string
operations.

This is the only defect so far found by CI rather than by a person, and it is a good argument for
the Linux and macOS runners.

---

## Recurring theme

Every entry above was found by **running the software**, not by reading it, and not by the unit
tests — which numbered in the hundreds and passed throughout. Cross-compilation proved the code
built for five runtimes while three of these defects sat in it. A test suite and a green build say
nothing about whether the first run on a fresh account puts your bearer token in the right place.
