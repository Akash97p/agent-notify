# Outbound channels

Outbound delivery is secondary to AgentNotify's local SQLite notification record. A channel failure never removes or changes the local notification. Every provider and route is disabled until a user explicitly enables it.

Use **Tray icon → Settings → Channels** to create a provider profile, enter encrypted values, send a test, and add a route. New profiles and routes begin disabled. Password fields never reveal stored values: leave one blank to preserve it, enter a value to replace it, or select the explicit removal checkbox for an optional credential. Route message content is excluded by default and requires explicit opt-in.

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

## Telegram Bot

Implementation status: adapter and native Settings fields complete; real-bot and human UI smoke pending.

Create a bot through Telegram's `@BotFather`, start a private conversation with it or grant it permission in the destination group/channel, and obtain the numeric chat ID or `@channelusername`. AgentNotify stores both the bot token and destination chat as DPAPI current-user encrypted secrets. A forum topic/thread ID is optional.

```json
{
  "botTokenSecretName": "bot_token",
  "chatIdSecretName": "chat_id",
  "messageThreadId": 42,
  "disableNotification": false,
  "protectContent": true
}
```

The `telegram` adapter calls only the official `https://api.telegram.org/bot<token>/sendMessage` endpoint. It disables redirects, cookies, ambient proxies, automatic decompression, and link previews; verifies that every DNS result is public immediately before connecting; and bounds a success response to 64 KiB. The bot token is required in Telegram's API URL but is never written to AgentNotify logs, SQLite configuration, notification metadata, or the JSON body.

Messages are sent as plain text without `parse_mode`, so notification content cannot inject Telegram markup. Text is safely truncated to Telegram's 4096-character limit without splitting a UTF-16 surrogate pair. The route's **Include notification message off-device** control is honored. Content protection defaults on; silent delivery is optional.

HTTP 408, 425, 429, 5xx, network failures, and malformed success responses retry. Redirects and other 4xx responses are permanent failures. Telegram's official `sendMessage` parameters and limits are documented in the [Telegram Bot API](https://core.telegram.org/bots/api#sendmessage).

## Discord incoming webhook

Implementation status: adapter and native Settings fields complete; real-webhook and human UI smoke pending.

Create an incoming webhook in the destination Discord channel and paste its complete URL into Settings. The URL contains its authentication token, so AgentNotify stores the entire value only as a DPAPI current-user encrypted secret. An optional numeric thread ID routes messages into an existing thread.

```json
{
  "webhookUrlSecretName": "webhook_url",
  "username": "AgentNotify",
  "threadId": "123456789012345678"
}
```

The `discord` adapter accepts only HTTPS URLs on `discord.com` with an exact incoming-webhook path; URI credentials, custom ports, existing query strings/fragments, malformed webhook IDs, and malformed tokens are rejected. The transport disables redirects, cookies, ambient proxies, and automatic decompression, validates that every Discord DNS result is public at connection time, and never reads a response body. It sends with `wait=true` so Discord confirms persistence.

Message content is bounded to Discord's 2000-character limit and safely escapes user-provided Discord Markdown. `allowed_mentions.parse` is always empty, preventing notification content from pinging users, roles, `@everyone`, or `@here`. Route-level message redaction is honored, and the encrypted webhook URL/token never appears in the JSON body, diagnostics, or logs.

HTTP 408, 425, 429, 5xx, and network failures retry; redirects and other 4xx responses are permanent. See Discord's official [Execute Webhook](https://docs.discord.com/developers/resources/webhook#execute-webhook) and [rate-limit](https://docs.discord.com/developers/topics/rate-limits) references.

## Slack incoming webhook

Implementation status: adapter and native Settings fields complete; real-webhook and human UI smoke pending.

Create an incoming webhook for the target Slack conversation and paste its complete URL into Settings. The URL is a credential tied to that workspace and channel, so AgentNotify stores it only as a DPAPI current-user encrypted secret. An optional `thread_ts` value sends the notification as a reply to an existing parent message.

```json
{
  "webhookUrlSecretName": "webhook_url",
  "threadTimestamp": "1712345678.123456"
}
```

The `slack` adapter accepts only exact HTTPS incoming-webhook paths on `hooks.slack.com` and the official GovSlack `hooks.slack-gov.com` host. Custom ports, URI credentials, queries, fragments, and malformed workspace/service/token segments are rejected. Redirects, cookies, ambient proxies, and automatic decompression are disabled, and every resolved address must be public at socket-connect time.

Messages set `mrkdwn: false` and `link_names: 0`. User-controlled `&`, `<`, and `>` characters are encoded so Slack control sequences cannot create user, channel, or broadcast mentions. Text is bounded to 4000 characters without splitting surrogate pairs and honors route-level message redaction. A 2xx response is accepted only with Slack's documented plain-text `ok` acknowledgement (or an empty 204); the response is bounded to 8 KiB and never logged.

HTTP 408, 425, 429, 5xx, network failures, and malformed success responses retry. Redirects and other 4xx responses are permanent. Slack documents webhook setup, secret handling, thread replies, and errors in [Sending messages using incoming webhooks](https://api.slack.com/messaging/webhooks), and documents `Retry-After` behavior in [Rate limits](https://api.slack.com/apis/rate-limits).

## Microsoft Teams Workflows

Implementation status: adapter and native Settings fields complete for current global-cloud workflow URLs; real-workflow, sovereign-cloud, and human UI smoke pending.

In Teams, create a Workflows template that receives a webhook and posts to a channel or chat, then copy the current trigger URL into Settings. The signed URL is a credential and may exceed 255 characters, so AgentNotify stores the complete value only as a DPAPI current-user encrypted secret and accepts up to 8192 characters.

```json
{
  "webhookUrlSecretName": "webhook_url"
}
```

The `teams` adapter accepts only the post-migration global Power Platform host suffix `*.environment.api.powerplatform.com`, the expected `/powerautomate/automations/direct/workflows/.../triggers/manual/paths/invoke` path, and a signed query containing `api-version`, `sp`, `sv`, and `sig`. Legacy Office 365 connectors and retired `logic.azure.com` URLs are rejected. Sovereign-cloud endpoints require a future explicit allowlist rather than weakening host validation.

Messages use Microsoft's documented `type: message` envelope with one Adaptive Card 1.2 attachment. Title, optional route-approved message, priority, type, agent, and project are bounded independently. User Markdown is escaped and `<at>` tags are encoded; the adapter supplies no Teams mention entities or actions. Redirects, cookies, proxies, decompression, private/mixed DNS results, and response-body reads are disabled.

Any 2xx response succeeds, including asynchronous `202 Accepted`. HTTP 408, 425, 429, 5xx, and network failures retry; redirects and other 4xx responses are permanent. See Microsoft's [Teams webhook trigger](https://learn.microsoft.com/en-us/connectors/teams/#when-a-teams-webhook-request-is-received) and [trigger URL migration guidance](https://learn.microsoft.com/en-us/troubleshoot/power-platform/power-automate/flow-run-issues/triggers-troubleshoot#changes-to-http-or-teams-webhook-trigger-flows).

## Planned adapters

The authoritative implementation order and constraints are in [FEATURE_BACKLOG.md](FEATURE_BACKLOG.md). Each adapter gets its own topic branch, contract tests, provider-specific retry classification, security notes, and user documentation before it is merged to `dev`.
