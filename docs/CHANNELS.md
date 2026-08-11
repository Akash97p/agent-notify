# Outbound channels

Outbound delivery is secondary to AgentNotify's local SQLite notification record. A channel failure never removes or changes the local notification. Every provider and route is disabled until a user explicitly enables it.

Use **Tray icon → Settings → Channels** to create a webhook profile, enter encrypted values, send a test, and add a route. New profiles and routes begin disabled. Password fields never reveal stored values: leave one blank to preserve it, enter a value to replace it, or select the explicit removal checkbox for an optional credential. Route message content is excluded by default and requires explicit opt-in.

## Generic HTTPS webhook

Implementation status: adapter and native Settings management UI complete; human visual/accessibility inspection pending.

The `webhook` adapter sends an HTTPS `POST` with `application/json`. Its endpoint URL is always a DPAPI-encrypted secret because real webhook URLs commonly contain credentials in their path or query. The adapter never logs the URL, headers, body, response body, or exception text.

Non-secret profile configuration:

```json
{
  "urlSecretName": "endpoint_url",
  "allowPrivateNetwork": false,
  "headers": {
    "X-Source": "agentnotify"
  },
  "secretHeaders": {
    "Authorization": "authorization"
  },
  "signature": {
    "secretName": "hmac_secret",
    "headerName": "X-AgentNotify-Signature",
    "timestampHeaderName": "X-AgentNotify-Timestamp"
  },
  "bodyTemplate": {
    "event": "agentnotify.notification",
    "deliveryId": "{{outbox_id}}",
    "notificationId": "{{notification_id}}",
    "notification": "{{payload}}"
  }
}
```

Encrypted secret names for that example:

- `endpoint_url` — required absolute HTTPS endpoint.
- `authorization` — optional value copied to the configured secret header.
- `hmac_secret` — optional key for HMAC-SHA-256 signing.

`{{payload}}` must be the complete value of a template field or array item and expands to the structured notification object. The ID placeholders may appear inside strings. Without `bodyTemplate`, the adapter sends the route-filtered notification payload directly.

Security behavior:

- HTTPS is mandatory; URI credentials and fragments are rejected.
- Redirects, cookies, ambient proxies, and automatic decompression are disabled.
- DNS is resolved at connection time and every resolved address must pass destination policy, preventing mixed public/private DNS rebinding.
- Loopback, private, carrier-grade NAT, and unique-local destinations require `allowPrivateNetwork: true`. Link-local/cloud-metadata, multicast, unspecified, benchmarking, and documentation ranges remain blocked.
- The caller-controlled `Host`, content framing, proxy, cookie, and idempotency headers are rejected. `Authorization` must come from encrypted secrets.
- Each request carries `Idempotency-Key: <outbox id>`. Optional signatures cover `<unix timestamp>.<exact body>` and use lowercase `sha256=<hex>`.
- 2xx succeeds; 408, 425, 429, 5xx, and network errors retry; redirects and other 4xx responses are permanent failures. Response bodies are not consumed or persisted.
- The dispatcher provides the outer 15-second timeout, six-attempt ceiling, jittered backoff, dead-letter state, and sanitized attempt history.

This transport follows the long-lived client/custom connection guidance in Microsoft's [HttpClient guidelines](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) and validates the socket opened through [`SocketsHttpHandler.ConnectCallback`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback).

## SMTP email

Implementation status: adapter and native Settings fields complete; real-server interoperability and visual/accessibility inspection pending.

The `smtp` adapter sends a bounded plain-text email to one to ten explicitly configured recipients. It never discovers recipients from notification metadata and never inherits Windows credentials. SMTP username and password/app-password values are DPAPI-encrypted; server, sender, recipients, and subject prefix are non-secret profile configuration.

```json
{
  "host": "smtp.example.com",
  "port": 587,
  "security": "start_tls",
  "allowPrivateNetwork": false,
  "fromAddress": "agentnotify@example.com",
  "fromName": "AgentNotify",
  "recipients": ["owner@example.com"],
  "subjectPrefix": "[AgentNotify] ",
  "usernameSecretName": "username",
  "passwordSecretName": "password"
}
```

Choose `start_tls` for port 587 or `tls` for TLS-on-connect, commonly port 465. Opportunistic modes (`auto`, plaintext, or STARTTLS-when-available) are rejected. Certificate validation and revocation checking remain enabled, TLS is restricted to TLS 1.2/1.3, and the server must successfully negotiate encryption before authentication.

DNS is resolved before connecting and every address must pass the same public/private policy as webhooks. The validated addresses are connected directly while the original hostname is retained for TLS certificate validation, closing the usual DNS-rebinding gap. Private/loopback SMTP requires explicit consent; link-local metadata and non-unicast ranges remain blocked.

Each delivery uses a stable Message-ID derived from the outbox ID. SMTP 4xx failures retry; 5xx, authentication, invalid configuration, and TLS-policy failures dead-letter. Network/protocol interruptions retry within the dispatcher's timeout and six-attempt ceiling. Message bodies honor the route's **Include notification message off-device** setting.

AgentNotify uses [MailKit 4.17.0](https://www.nuget.org/packages/MailKit/4.17.0) and its strict `StartTls`/`SslOnConnect` modes. Redistribution notices are in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

## Planned adapters

The authoritative implementation order and constraints are in [FEATURE_BACKLOG.md](FEATURE_BACKLOG.md). Each adapter gets its own topic branch, contract tests, provider-specific retry classification, security notes, and user documentation before it is merged to `dev`.
