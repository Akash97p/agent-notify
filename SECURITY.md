# Security policy

## Supported versions

Security fixes target the latest released AgentNotify version. This repository currently represents prerelease version `0.2.0-alpha.3`; the first mature release is reserved for `1.0.0`.

## Reporting a vulnerability

Do not disclose an exploitable issue in a public GitHub issue. Contact the repository owner through a private GitHub contact channel and include:

- affected AgentNotify version and operating-system version;
- reproduction steps or a minimal proof of concept;
- expected and observed behavior;
- impact, especially whether another local process can read or modify notifications; and
- suggested mitigation, if known.

Avoid including real bearer tokens, notification content, database files, or other personal data.

## Published review

A pre-release source review of this version is published in
[audit_2026-09-19.md](audit_2026-09-19.md). It records what the product is trusted with, what leaves
the machine, the then-open findings with code references, and the controls that hold up. It is a
point-in-time review rather than a certification, and the open findings are tracked in
[TODO.md](TODO.md).

## Trust boundary

AgentNotify assumes the signed-in operating-system user controls processes in that user session. Its API is protected by:

- binding exclusively to `127.0.0.1`;
- a randomly generated per-user bearer token;
- bounded request size and field validation;
- a create-request rate limit; and
- local persistence under the user profile.

The token prevents accidental or unsophisticated calls from unrelated local software. It is not a defense against malware already running as the same OS user, which can generally read that user’s files and process environment.

`config.json` contains the token: `%LOCALAPPDATA%\AgentNotify\config.json` on Windows and `$XDG_DATA_HOME/AgentNotify/config.json` (normally `~/.local/share/AgentNotify/config.json`) on macOS/Linux. Do not attach it to issues, commit it, print it in agent output, or send it to external services. Logs intentionally omit the token.

### Web interface

The web interface at `/ui/` shares the loopback listener and, deliberately, asks for no sign-in: like
the Windows tray app, it trusts the person using the computer. Anyone who can reach the loopback
port can use it, which includes other local accounts on a shared machine. It defends against other
web sites instead: requests naming any host other than a loopback address are refused (DNS
rebinding), even when an SSH local forward presents a different browser-side port. State changes
need a custom header and an `Origin` matching that exact loopback host and port; a strict content
security policy applies. Stored channel secrets and question nonces are never sent to the page. The
`/v1` agent API still requires the bearer token. Details: [docs/WEB_UI.md](docs/WEB_UI.md).

The Usage page reads local, read-only activity records from Claude Code, Codex, OpenCode, the
Kilo CLI, Muse Code, and the Gemini CLI; on Windows it also reads those sources inside running WSL
distributions. Database queries and file parsers select usage scalars and identifiers, not prompt or
response payloads. The endpoint returns aggregate token counts, model identifiers, project folder
names with opaque IDs, and a published-rate cost estimate. It never returns full project paths, log
paths, prompt/response text, or provider credentials. It does not contact agent providers or billing
APIs.

Live quota is a separate on-demand feature. Codex quota is requested through the locally
installed Codex app-server RPC, which owns its authentication. Claude Code quota uses a bounded
read-only request to the fixed `https://api.anthropic.com/api/oauth/usage` endpoint with the
current local OAuth access token. AgentNotify never stores, refreshes, logs, or returns that
token, disables redirects and ambient proxies, and gives generic failure messages. The response
is reduced to percentages, reset times, optional plan/credit data, source, and freshness. The
Claude endpoint is a first-party implementation dependency without a stable public API contract;
quota failure never affects notifications or local Usage. Cross-origin pages cannot force manual
refresh because it is a same-origin-header-protected POST.

The macOS quota status item is a separate same-user local client of that WebUI boundary. The broker
launches it with only the loopback port. It receives normalized account labels, percentages, reset
times, plan/credit values, freshness, source, and presentation settings; it receives no bearer token,
profile path, account email, credential, or provider response. It does not read `config.json` or agent
credential files. Because it polls periodically while enabled, quota checks on macOS are not purely
on-demand; the existing per-account cache, request coalescing, timeout, response cap, stale handling,
and rate limits still apply. Disable the status item under Live quota to stop that background polling.
Like the WebUI, this is not a boundary against another process already running as the same OS user.

Additional quota profiles store only a label and agent profile directory in the owner-only
configuration file. The WebUI accepts absolute directories under the broker user's home folder,
never passwords or token text. The profile-management page returns these paths to its local owner;
the quota report still returns no credential paths or account emails. Codex credentials stay with
Codex in the selected `CODEX_HOME`. The Claude probe reads the selected agent-owned credential
file without copying it into AgentNotify storage. OpenCode Go estimates read only local usage
scalars and are labeled as incomplete local observations, never provider-confirmed balance.

API-account balance/spend checks are also explicit and on demand. Keys are encrypted, write-only,
and sent only to the selected provider's fixed official host. Prefer a dedicated least-privilege key:
OpenAI and Anthropic Admin keys can manage an entire organization, not merely read a personal
balance, and any process already running as this OS user can ask AgentNotify to use stored
credentials. The WebUI requires a separate acknowledgement before saving one.

### Provider router

The model router is an optional local proxy and is off by default. When it is off, `/router` returns
`404` and AgentNotify makes no model-provider request. When enabled it accepts OpenAI Responses,
OpenAI Chat Completions, and Anthropic Messages shapes on loopback, translates when needed, and sends
the prompt/conversation content to the configured upstream provider. Enabling it therefore expands
the trust boundary beyond local notification data: prompts, tool arguments/results, images, and any
other supported request content necessarily leave the machine for the provider selected by routing.

Router protections are deliberately separate from notification access:

- `/router` remains loopback-only, rejects non-loopback `Host` values, and refuses browser `Origin`
  requests; its maximum body size is independently bounded.
- A separate router key grants the ability to spend through configured providers but cannot read the
  `/v1` notification API or WebUI configuration. Treat it as a local spending credential.
- Upstream API keys are encrypted with the same platform protector as channel secrets, are
  write-only, and are decrypted only while creating the upstream request.
- Upstreams require HTTPS, except explicit loopback HTTP for a local model server. Redirects, cookies,
  and system proxies are disabled so a credential cannot follow an unexpected destination.
- The SQLite ledger records routing, model, status, duration, usage, and stable error codes, but never
  prompts, responses, headers, keys, or provider error bodies.
- Claude Code's native Anthropic credential is forwarded only to Anthropic for an otherwise-unrouted
  native Claude model. It is never stored, logged, entered in the ledger, or sent to another upstream.
- ChatGPT-plan and Muse Code subscription integrations reuse another tool's local sign-in through
  undocumented provider interfaces. They are clearly labeled unofficial and remain opt-in.

Connecting Codex or Claude Code can modify that agent's configuration only after an explicit local
UI/CLI action; AgentNotify first creates a restorable copy. See [docs/ROUTER.md](docs/ROUTER.md) for
the exact routes, formats, failover rules, subscription caveats, and file changes.

## External-channel requirements

The nineteen implemented email, chat, SMS, push, webhook, MQTT, and Relay adapters are opt-in extensions; local-only operation remains the default. Any new adapter or material transport change must be separately reviewed for:

- explicit opt-in and destination verification;
- provider credential storage using the platform secret protector;
- notification-content redaction and user-configurable allowlists;
- retry limits, idempotency, cost controls, and provider rate limits;
- transport encryption and certificate validation;
- auditing without logging secrets or sensitive message content; and
- a clear local-only mode that remains the default.

Provider profiles, routes, outbox state, and delivery attempts are stored in SQLite. Credentials must first be encrypted with a versioned platform secret envelope (Windows DPAPI or the Unix protections below). Plaintext secrets must exist only for the minimum time required to configure or call a provider, and must never appear in list responses, exports, exception messages, notification metadata, analytics, or logs.

The implemented envelope prefix is `dpapi-user:v1:` and protection uses application-specific optional entropy. See Microsoft’s [ProtectedData documentation](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata.protect).

### Secret protection outside Windows

DPAPI has no equivalent on macOS or Linux, so the portable broker selects a protector at startup. Windows always uses DPAPI and must never fall back to any other scheme while it is available.

| Platform | Protection | Envelope |
| --- | --- | --- |
| Windows | DPAPI, current-user scope | `dpapi-user:v1:` |
| macOS | AES-GCM under a 256-bit key in the login keychain (`/usr/bin/security`) | `aes-gcm:v1:` |
| Linux | AES-GCM under a 256-bit key in the Secret Service keyring (`secret-tool`) | `aes-gcm:v1:` |
| macOS/Linux without a keyring | AES-GCM under a `0600` key file in the config directory | `aes-gcm:v1:` |

The key-file fallback is the weakest of these and is deliberately visible: the broker logs a warning at startup and `agentnotifyd` prints one to the console. Any process running as the same user can read `secret.key` and therefore decrypt stored provider credentials. It exists so a machine with no keyring still runs, not because it is equivalent protection.

A corrupted key file is a hard error rather than a silent regeneration, because a new key would make every stored provider credential undecryptable.

On Unix the per-user data directory is created `0700`, and `config.json` (which holds the local bearer token), `agentnotify.db`, and `secret.key` are created `0600`. Windows relies on the per-user profile ACL as before. `Environment.SpecialFolder.LocalApplicationData` returns an empty string on Unix when the base directory does not yet exist, so the data directory is always resolved to an absolute path — otherwise the token and database would be written into whatever working directory the broker was started from.

DPAPI protects data at rest from other users and casual file disclosure. It does not defend against malware already executing as the same Windows user. Backups containing encrypted credentials may not be decryptable under another user profile or machine; migration/export tooling must omit secrets by default and require re-entry.

Every outbound adapter must use TLS by default, validate certificates, bound response bodies and timeouts, redact sensitive URLs/headers, and apply provider-specific retry and idempotency rules. Generic endpoints and self-hosted services require explicit destination validation so a compromised local caller cannot turn AgentNotify into an unrestricted network proxy.

The generic webhook adapter stores its complete endpoint URL as an encrypted secret, requires HTTPS, disables redirects/cookies/proxies, rejects unsafe headers, and validates every DNS result at socket-connect time. Private destinations require explicit opt-in; link-local/cloud-metadata and non-unicast ranges remain blocked even then. HMAC signing uses a separately encrypted key. See [docs/CHANNELS.md](docs/CHANNELS.md).

The SMTP adapter requires authenticated strict STARTTLS or TLS-on-connect; opportunistic encryption is rejected. It connects a prevalidated destination socket while retaining the configured hostname for certificate validation, checks revocation, permits only TLS 1.2/1.3, and sends only to the profile's explicit recipient allowlist. It never uses notification metadata as an address source or inherits ambient credentials.

The Telegram adapter stores both bot token and chat destination as encrypted secrets and connects only to the official `api.telegram.org` HTTPS endpoint. Redirects, cookies, proxies, private/mixed DNS results, link previews, and markup parsing are disabled. Success responses are bounded, request failures are reduced to stable error codes, and the token is never included in logs or request JSON.

The Discord adapter stores the full token-bearing webhook URL as an encrypted secret, accepts only an exact official `discord.com` HTTPS webhook shape, and validates public DNS results at connect time. Redirects, cookies, proxies, response-body reads, and all allowed mentions are disabled. User-controlled Markdown is escaped and the API is called with `wait=true` to obtain delivery confirmation.

The Slack adapter stores the complete channel-bound webhook URL as an encrypted secret and accepts only exact incoming-webhook paths on Slack or GovSlack's official hook hosts. It disables redirects, cookies, proxies, Markdown, automatic link-name expansion, and private/mixed DNS results. Slack control-sequence delimiters are encoded; acknowledgements are bounded and reduced to stable status codes without logging response content.

The Teams adapter stores the complete signed Workflows trigger URL as an encrypted secret. It accepts only the current global Power Platform host suffix, expected trigger path, and required signature parameters; legacy connector/retired Logic Apps URLs are rejected. Adaptive Card text and fields are bounded, Markdown and mention tags are escaped, and redirects, proxies, private/mixed DNS results, and response-body reads are disabled.

The Zoho Cliq adapter stores the complete `zapikey` URL as an encrypted secret, recognizes only the nine official regional Cliq hosts, validates exact channel/bot message paths, and rejects unknown query parameters. Message formatting controls are escaped; redirects, proxies, private/mixed DNS results, and response-body reads are disabled.

The Google Chat adapter stores the complete `key`/`token` webhook URL as an encrypted secret and accepts only the exact official `chat.googleapis.com/v1/spaces/{space}/messages` shape. Unknown query parameters are rejected before sending. User-controlled Chat formatting and mention delimiters are neutralized, serialized payloads are bounded below Google's 32,000-byte limit, and redirects, proxies, private/mixed DNS results, and response-body reads are disabled.

The Mattermost adapter stores its token-bearing webhook URL as an encrypted secret, requires HTTPS and an exact terminal `/hooks/{token}` path, and keeps platform certificate validation enabled for self-hosted servers. Public destinations are the default; private or loopback servers require explicit consent, while link-local/cloud-metadata and non-unicast ranges remain blocked. Mattermost and Slack-compatible mention controls are neutralized, Markdown is escaped, acknowledgements are bounded, and redirects, proxies, and any DNS result outside the configured destination policy are rejected.

The Matrix adapter encrypts both the access token and exact room ID, uses bearer-header authentication rather than the deprecated query parameter, and derives a stable non-secret transaction ID for idempotent retries. Homeservers require HTTPS, standard certificate validation, and explicit consent for private addressing. Plain-text `m.room.message` events carry an empty `m.mentions` object and neutralize legacy `@` matching. End-to-end encrypted rooms are not supported and must not be configured.

The ntfy adapter encrypts the topic and optional access token, publishes JSON to the server base URL so neither appears in the request URL, and requires explicit consent when no token is configured. Self-hosted private servers require separate network consent and standard TLS validation. Stable sequence IDs make retries update rather than duplicate a notification; message bytes are capped at 4096 so ntfy does not silently convert oversized notification text into an attachment. Users must understand that unauthenticated ntfy.sh topics are public.

The Gotify adapter encrypts the application token and sends it only in `X-Gotify-Key`, never in the query string. Self-hosted servers require HTTPS, normal certificate validation, and explicit private-network consent. Messages are forced to `text/plain`; remote images, click URLs, Android intents, and action extras are intentionally omitted so agent-provided content cannot turn a notification into an external request or executable client action. Gotify has no documented request-idempotency key, so an ambiguous network failure can still produce a duplicate.

The Pushover adapter encrypts the application token, user/group key, and optional device restriction. It posts URL-encoded plain text only to the exact official `api.pushover.net` HTTPS message endpoint, validates every DNS result at connection time, and disables redirects, cookies, proxies, and decompression. Critical-to-emergency mapping is opt-in because it repeats alerts until acknowledgement or expiry. Pushover declares all 4xx responses non-retryable; AgentNotify follows that rule, including quota-related 429 responses. The API has no request idempotency key, so an ambiguous network failure can still produce a duplicate.

The Pushbullet adapter encrypts the personal access token and optional device/channel/email target. The token grants full account access, so it is never returned by profile reads or placed in a URL/body. Requests go only to the official HTTPS Pushes endpoint through a public-DNS-only connection. The adapter sends plain `note` objects without file uploads, remote URLs, or source/client targeting; a stable opaque `guid` reduces retry duplicates. Email targeting is explicit because Pushbullet may fall back to ordinary email delivery. Pushbullet describes GUID idempotency as only “mostly” idempotent, so duplicates remain possible after ambiguous failures.

The Twilio SMS adapter encrypts the Account SID, API-key SID/secret or Auth Token, single recipient, and sender. Standard/restricted API keys are recommended; Account SID/Auth Token mode is labeled for local testing only. Paid sending requires explicit consent and a minimum-priority floor. AgentNotify emits one text-only SMS segment, requests content discard/address obfuscation, leaves Twilio risk checks enabled, and never supplies media, callbacks, or active links. Because the create-message API has no documented idempotency mechanism, all handled ambiguous network/timeout/5xx/success failures are terminal rather than retried; only a definite 429 rate-limit response retries. A process or machine failure after provider acceptance but before local completion can still be recovered and replayed. Users remain responsible for Twilio geo permissions, sender registration, usage triggers, legal consent, and a durable account spend ceiling.

The WhatsApp Cloud adapter encrypts the Meta system-user token, phone-number ID, and single E.164 recipient. It connects only to the exact public `graph.facebook.com/{version}/{phone-number-id}/messages` endpoint and accepts a strictly validated configurable Graph version. A profile cannot be saved or used without explicit recipient-opt-in, approved-template, and paid-send acknowledgements. AgentNotify sends only approved text templates with an allowlisted fixed-field variable mapping; it never sends free-form session messages, media, interactive buttons, URLs, or agent-selected recipients. A critical-only cost floor is the default. Meta documents no create-message idempotency key, so handled network/timeout/5xx/malformed-success ambiguity is terminal and only a definite 429 retries. Process or machine failure after provider acceptance can still replay. Users remain responsible for Meta business verification, template approval, recipient consent and opt-out handling, billing, quality rating, token rotation, and account controls.

The Twilio WhatsApp adapter encrypts the Account and credential SIDs/secrets, single E.164 recipient, WhatsApp-enabled Messaging Service SID, and approved Content SID. A profile requires separate recipient-opt-in, approved-template, text-only-template, and paid-send acknowledgements. The adapter supplies only `ContentSid`, an allowlisted numbered `ContentVariables` object, and `whatsapp:` recipient through the official Messages endpoint; it never supplies a free-form body, sender override, media, dynamic URL, or callback. A critical-only cost floor is the default. Handled ambiguous results do not retry because Twilio documents no create-message idempotency key; only a definite 429 retries. Process or machine failure after provider acceptance can still replay. Operators remain responsible for Twilio/Meta onboarding, template state, opt-outs, billing, quality, token rotation, and account controls.

The MQTT adapter encrypts the exact non-wildcard topic, username/password, and optional client-certificate thumbprint. TLS 1.2/1.3 with normal Windows chain, hostname, and revocation validation is mandatory; there is no certificate-bypass setting. Every DNS answer is validated against the explicit public/private network policy, then MQTTnet connects to a pinned IP endpoint while using the configured broker host for SNI and certificate verification. mTLS private keys remain in the Windows Current User Personal certificate store and are never copied into SQLite; selection requires a currently valid private-key certificate with compatible digital-signature and client-authentication usage. Publishes are MQTT 5 JSON, non-retained, bounded to 16 KiB, and carry stable opaque delivery identifiers for consumer deduplication. QoS 0 is not replayed after handled ambiguity; QoS 1/2 require explicit duplicate-risk acknowledgement because a new AgentNotify outbox attempt or session can still duplicate despite protocol-level guarantees. Anonymous TLS requires separate acknowledgement. Link-local, metadata, multicast, documentation, and mixed-policy DNS answers remain blocked.

The AgentNotify Relay adapter and pairing client share one hardened transport: redirects, cookies,
ambient proxies, and automatic decompression are disabled; every DNS answer is checked immediately
before a pinned-IP connection; private destinations require explicit consent; and HTTP is accepted
only for consented localhost development. Pairing first verifies the Relay discovery document and
requires the returned browser approval URL to have the configured Relay's exact scheme, host, and
port. The poll credential remains only in memory, is sent only in an authorization header, and is
discarded on completion or cancellation. The one-time installation credential is validated,
self-tested, never rendered, and encrypted through the same provider secret store used by delivery.
Both response sizes and call times are bounded, and sanitized exceptions cannot contain either
credential. The manual credential field remains collapsed under Advanced for explicitly provisioned
or headless recovery workflows.

Never extend the current bearer token directly to an internet-facing API.
