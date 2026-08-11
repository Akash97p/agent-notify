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

## Planned adapters

The authoritative implementation order and constraints are in [FEATURE_BACKLOG.md](FEATURE_BACKLOG.md). Each adapter gets its own topic branch, contract tests, provider-specific retry classification, security notes, and user documentation before it is merged to `dev`.
