# Security policy

## Supported versions

Security fixes target the latest released AgentNotify version. This repository currently represents version 1.0.0.

## Reporting a vulnerability

Do not disclose an exploitable issue in a public GitHub issue. Contact the repository owner through a private GitHub contact channel and include:

- affected version and Windows version;
- reproduction steps or a minimal proof of concept;
- expected and observed behavior;
- impact, especially whether another local process can read or modify notifications; and
- suggested mitigation, if known.

Avoid including real bearer tokens, notification content, database files, or other personal data.

## V1 trust boundary

AgentNotify assumes the signed-in Windows user controls processes in that user session. Its API is protected by:

- binding exclusively to `127.0.0.1`;
- a randomly generated per-user bearer token;
- bounded request size and field validation;
- a create-request rate limit; and
- local persistence under the user profile.

The token prevents accidental or unsophisticated calls from unrelated local software. It is not a defense against malware already running as the same Windows user, which can generally read that user’s files and process environment.

`%LOCALAPPDATA%\AgentNotify\config.json` contains the token. Do not attach it to issues, commit it, print it in agent output, or send it to external services. Logs intentionally omit the token.

## External-channel requirements

Email, WhatsApp, chat, SMS, push, LAN, and remote transports are not part of the 1.0 baseline. Any implementation must be separately reviewed for:

- explicit opt-in and destination verification;
- provider credential storage using Windows-protected secret storage;
- notification-content redaction and user-configurable allowlists;
- retry limits, idempotency, cost controls, and provider rate limits;
- transport encryption and certificate validation;
- auditing without logging secrets or sensitive message content; and
- a clear local-only mode that remains the default.

Provider profiles, routes, outbox state, and delivery attempts may be stored in SQLite. Credentials must first be encrypted with a versioned secret envelope backed by Windows DPAPI in current-user scope. Plaintext secrets must exist only for the minimum time required to configure or call a provider, and must never appear in list responses, exports, exception messages, notification metadata, analytics, or logs.

The implemented envelope prefix is `dpapi-user:v1:` and protection uses application-specific optional entropy. The DPAPI implementation is Windows-only by design; portable tests use an injected AES-GCM protector, not a production fallback. See Microsoft’s [ProtectedData documentation](https://learn.microsoft.com/dotnet/api/system.security.cryptography.protecteddata.protect).

DPAPI protects data at rest from other users and casual file disclosure. It does not defend against malware already executing as the same Windows user. Backups containing encrypted credentials may not be decryptable under another user profile or machine; migration/export tooling must omit secrets by default and require re-entry.

Every outbound adapter must use TLS by default, validate certificates, bound response bodies and timeouts, redact sensitive URLs/headers, and apply provider-specific retry and idempotency rules. Generic endpoints and self-hosted services require explicit destination validation so a compromised local caller cannot turn AgentNotify into an unrestricted network proxy.

The generic webhook adapter stores its complete endpoint URL as an encrypted secret, requires HTTPS, disables redirects/cookies/proxies, rejects unsafe headers, and validates every DNS result at socket-connect time. Private destinations require explicit opt-in; link-local/cloud-metadata and non-unicast ranges remain blocked even then. HMAC signing uses a separately encrypted key. See [docs/CHANNELS.md](docs/CHANNELS.md).

The SMTP adapter requires authenticated strict STARTTLS or TLS-on-connect; opportunistic encryption is rejected. It connects a prevalidated destination socket while retaining the configured hostname for certificate validation, checks revocation, permits only TLS 1.2/1.3, and sends only to the profile's explicit recipient allowlist. It never uses notification metadata as an address source or inherits ambient credentials.

The Telegram adapter stores both bot token and chat destination as encrypted secrets and connects only to the official `api.telegram.org` HTTPS endpoint. Redirects, cookies, proxies, private/mixed DNS results, link previews, and markup parsing are disabled. Success responses are bounded, request failures are reduced to stable error codes, and the token is never included in logs or request JSON.

Never extend the current bearer token directly to an internet-facing API.
