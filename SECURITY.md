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

Email, WhatsApp, chat, SMS, push, LAN, and remote transports are not part of V1. Any implementation must be separately reviewed for:

- explicit opt-in and destination verification;
- provider credential storage using Windows-protected secret storage;
- notification-content redaction and user-configurable allowlists;
- retry limits, idempotency, cost controls, and provider rate limits;
- transport encryption and certificate validation;
- auditing without logging secrets or sensitive message content; and
- a clear local-only mode that remains the default.

Never extend the current bearer token directly to an internet-facing API.
