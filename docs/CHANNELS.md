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

## Zoho Cliq

Implementation status: adapter and native Settings fields complete across all documented Cliq data centers; real-webhook and human UI smoke pending.

Generate a Webhook Token in **Bots & Tools**, choose a channel or bot message endpoint, and paste the complete generated URL into Settings. The `zapikey` query value authenticates the request, so AgentNotify stores the entire URL only as a DPAPI current-user encrypted secret.

```json
{
  "webhookUrlSecretName": "webhook_url"
}
```

The `zoho_cliq` adapter accepts only exact official Cliq hosts for the United States, Europe, India, Australia, China, Japan, Saudi Arabia, United Kingdom, and Canada. It supports documented v2 `channelsbyname`, `channels`, and `bots` message paths. The query must contain `zapikey`; only the optional documented `bot_unique_name` parameter may accompany it. URI credentials, custom ports, fragments, unrecognized parameters, and malformed destination identifiers are rejected.

Messages are plain bounded text. Cliq Markdown control characters are escaped, route-level message redaction is honored, and content is truncated without splitting surrogate pairs at the documented 5000-character channel/bot limit. Redirects, cookies, proxies, decompression, private/mixed DNS results, and response-body reads are disabled.

HTTP 408, 425, 429, 5xx, and network failures retry; redirects and other 4xx responses are permanent. See Zoho's official [Webhook Tokens](https://www.zoho.com/cliq/help/platform/webhook-tokens.html), [message API](https://www.zoho.com/cliq/help/restapi/v2/messages/), and [data-center list](https://www.zoho.com/cliq/help/restapi/v3/introduction/#multiple-data-centers).

## Google Chat

Implementation status: adapter and native Settings fields complete; real-webhook and human UI smoke pending.

In the target Google Chat space, open **Apps & integrations**, add an incoming webhook, and paste the complete copied URL into Settings. Its `key` and `token` query values authenticate access to that space, so AgentNotify stores the entire URL only as a DPAPI current-user encrypted secret.

```json
{
  "webhookUrlSecretName": "webhook_url",
  "threadKey": "agent-builds",
  "threadReplyPolicy": "fallback"
}
```

`threadKey` is optional. When set, `fallback` replies to the matching thread or starts it if it does not exist; `fail` posts only when the thread can be resolved. AgentNotify places the thread key in `thread.threadKey` and adds Google's documented `messageReplyOption`, rather than using the deprecated query-level `threadKey` parameter.

The `google_chat` adapter accepts only HTTPS URLs on `chat.googleapis.com` with the exact `/v1/spaces/{space}/messages` path and exactly one `key` and one `token`. Custom ports, URI credentials, fragments, unknown/duplicate parameters, encoded path separators, and malformed space identifiers are rejected before a request is sent.

Messages are route-redacted plain text. Chat formatting controls are escaped, and ASCII angle brackets are neutralized so notification content cannot inject `<users/all>`, user mentions, custom links, or other Chat control sequences. The complete serialized JSON body is kept at or below 31,500 UTF-8 bytes, leaving margin beneath Google's documented 32,000-byte message limit. Redirects, cookies, proxies, decompression, private/mixed DNS results, and response-body reads are disabled.

AgentNotify spaces requests at least one second apart before sending. HTTP 408, 425, 429, 5xx, and network failures retry through the durable dispatcher; redirects and other 4xx responses are permanent. Google documents the URL secret, payload, threading, error handling, and one-write-per-second shared space quota in [Build a Google Chat app as a webhook](https://developers.google.com/workspace/chat/quickstart/webhooks), the size limit in [`spaces.messages.create`](https://developers.google.com/workspace/chat/api/reference/rest/v1/spaces.messages/create), and mention syntax in [Format messages](https://developers.google.com/workspace/chat/format-messages).

## Mattermost

Implementation status: adapter and native Settings fields complete; real-server and human UI smoke pending.

Create an Incoming Webhook in the target Mattermost installation and paste its complete generated URL into Settings. The URL grants permission to post as its webhook integration, so AgentNotify stores it only as a DPAPI current-user encrypted secret.

```json
{
  "webhookUrlSecretName": "webhook_url",
  "allowPrivateNetwork": false,
  "silent": false
}
```

The `mattermost` adapter requires HTTPS and a path ending in `/hooks/{token}`. It supports deployments under a URL subpath and custom HTTPS ports. URI credentials, query strings, fragments, encoded path separators, malformed tokens, and non-HTTPS endpoints are rejected. Public destinations are allowed by default; a LAN, loopback, carrier-grade NAT, or IPv6 unique-local server requires the explicit **Allow private/loopback destinations** setting. Link-local/cloud-metadata, multicast, unspecified, benchmarking, and documentation ranges are always blocked.

The operating system's normal TLS hostname and certificate-chain validation is always active, including for private servers. AgentNotify does not offer an “accept invalid/self-signed certificate” switch; administrators should install their private CA correctly. Redirects, cookies, ambient proxies, and decompression are disabled, and every DNS result is checked immediately before connection to prevent public/private rebinding.

Messages use the webhook's configured destination and identity—AgentNotify does not override the channel, username, icon, or arbitrary props. Text is route-redacted, Markdown-escaped, and limited to Mattermost's documented 16,383 characters without splitting surrogate pairs. `@` and angle-bracket controls are neutralized to prevent `@channel`, `@here`, user, and Slack-compatible mentions from notification content. Optional silent mode asks Mattermost to suppress desktop, push, email, unread, mention-count, and new-message signals.

A 2xx response succeeds only when it is `204 No Content` or contains Mattermost's bounded plain-text `ok` acknowledgement. HTTP 408, 425, 429, 5xx, network failures, and malformed success acknowledgements retry; redirects and other 4xx responses are permanent. See Mattermost's official [Incoming webhooks](https://developers.mattermost.com/integrate/webhooks/incoming/) documentation for setup, payload fields, acknowledgement, mention behavior, silent mode, and post limits.

## Matrix

Implementation status: adapter and native Settings fields complete for unencrypted rooms; end-to-end encryption, real-server, and human UI smoke pending.

Configure the homeserver's HTTPS client base URL, a dedicated account access token, and the exact room ID beginning with `!`. The token and room ID are DPAPI current-user encrypted. The account must already be joined and allowed to send messages.

```json
{
  "homeserverBaseUrl": "https://matrix.example.org",
  "allowPrivateNetwork": false,
  "accessTokenSecretName": "access_token",
  "roomIdSecretName": "room_id"
}
```

The `matrix` adapter uses `PUT /_matrix/client/v3/rooms/{roomId}/send/m.room.message/{txnId}` with `Authorization: Bearer`; it never places the token in a query string. A stable SHA-256-derived transaction ID makes dispatcher retries idempotent without exposing the notification or outbox ID. The body contains `msgtype: m.text`, route-redacted text, and an explicit empty `m.mentions` object. ASCII `@` is neutralized as defense for older clients and servers that still inspect the body for mentions.

Homeserver subpaths and custom HTTPS ports are supported. URI credentials, queries, fragments, unsafe base paths, unconsented private addresses, and all link-local/cloud-metadata or non-unicast ranges are rejected. Platform TLS certificate validation remains mandatory. Serialized JSON is bounded to 48 KiB, leaving space beneath Matrix's 65,536-byte complete-event limit. A successful response must contain a bounded `$`-prefixed event ID.

End-to-end encrypted rooms are intentionally unsupported: AgentNotify does not possess or manage Matrix device/session keys, so a plaintext event sent to an encrypted room would not provide correct secure delivery. HTTP 408, 425, 429, 5xx, network failures, and malformed success acknowledgements retry; redirects and other 4xx responses are permanent. See the Matrix v1.19 Client-Server specification for [sending room events](https://spec.matrix.org/v1.19/client-server-api/#put_matrixclientv3roomsroomidsendeventtypetxnid), [access-token handling](https://spec.matrix.org/v1.19/client-server-api/#using-access-tokens), [mentions](https://spec.matrix.org/v1.19/client-server-api/#user-and-room-mentions), and [transaction identifiers](https://spec.matrix.org/v1.19/client-server-api/#transaction-identifiers).

## ntfy

Implementation status: adapter and native Settings fields complete; real-server and human UI smoke pending.

Enter an HTTPS ntfy server base URL, topic, and preferably a dedicated access token. The topic and token are DPAPI current-user encrypted. AgentNotify publishes the official JSON form to the server base URL with the topic in the body, so sensitive or high-entropy topic names do not appear in URLs, proxy access logs, or sanitized diagnostics.

```json
{
  "serverBaseUrl": "https://ntfy.example.org",
  "allowPrivateNetwork": false,
  "allowUnauthenticatedTopic": false,
  "topicSecretName": "topic",
  "accessTokenSecretName": "access_token"
}
```

Current ntfy `tk_` access tokens are sent through the `Authorization: Bearer` header. Publishing without a token is rejected unless **Allow unauthenticated publishing** is explicitly selected. This warning matters especially for ntfy.sh: unprotected topics are public and anyone who guesses or learns the topic can subscribe or publish. A long random topic reduces guessing but is not equivalent to access control.

Self-hosted URL subpaths and custom HTTPS ports are supported. Private or loopback servers require separate private-network consent. HTTP, URI credentials, queries, fragments, unsafe base paths, link-local/cloud-metadata addresses, and any DNS result outside the selected policy are rejected. Certificate and hostname validation cannot be disabled.

AgentNotify maps low/normal/high/critical to ntfy priorities 2/3/4/5, supplies conservative fixed emoji tags, disables Markdown, and includes a stable non-secret sequence ID so a retry updates the prior notification rather than duplicating it. The message is capped at 4096 UTF-8 bytes to avoid ntfy's documented conversion of longer messages into attachments. Route-level message redaction is honored.

A successful response must contain a bounded JSON message ID and, when present, the `message` event kind. HTTP 408, 425, 429, 5xx, network failures, and malformed success responses retry; redirects and other 4xx responses are permanent. See ntfy's official [publishing documentation](https://docs.ntfy.sh/publish/), [API limits](https://docs.ntfy.sh/publish/#limitations), and [access-control documentation](https://docs.ntfy.sh/config/#access-control).

## Gotify

Implementation status: adapter and native Settings fields complete; real-server and human UI smoke pending.

Create a dedicated application in Gotify and enter the server's HTTPS base URL and application token. Starting with Gotify 3, application tokens are shown only when created or rotated, so store the value when Gotify presents it. AgentNotify encrypts it with DPAPI and sends it only through the documented `X-Gotify-Key` header.

```json
{
  "serverBaseUrl": "https://push.example.org/gotify",
  "allowPrivateNetwork": false,
  "applicationTokenSecretName": "application_token"
}
```

The base URL can include a reverse-proxy subpath and custom HTTPS port, but must not include the terminal `/message`; AgentNotify appends it. HTTP endpoints, URI credentials, queries, fragments, unsafe paths, unconsented private addresses, and all link-local/cloud-metadata or non-unicast destinations are rejected. TLS certificate and hostname validation remains mandatory.

Title and route-redacted message text are bounded, and AgentNotify maps low/normal/high/critical priorities to 2/5/7/10. The payload explicitly sets Gotify's `client::display.contentType` to `text/plain`. It does not add Markdown, images, click URLs, Android intents, or action extras. This follows Gotify's warning that untrusted Markdown can cause information disclosure by loading remote images.

A successful response must contain a positive bounded Gotify message ID. HTTP 408, 425, 429, 5xx, network failures, and malformed success responses retry; redirects and other 4xx responses are permanent. Gotify does not document an idempotency key, so an ambiguous timeout after server acceptance can produce a duplicate. See Gotify's official [Push messages](https://gotify.net/docs/pushmsg), [Message Extras](https://gotify.net/docs/msgextras), and [API documentation](https://gotify.net/api-docs).

## Pushover

Implementation status: adapter and native Settings fields complete; real-account, receipt polling, and human UI smoke pending.

Register a dedicated application in Pushover, then enter its 30-character application API token and the destination's 30-character user or delivery-group key. AgentNotify stores both values—and an optional single-device restriction—as DPAPI current-user encrypted secrets. Each installation should use an end-user-owned Pushover application token; AgentNotify does not embed or distribute a shared token.

```json
{
  "applicationTokenSecretName": "application_token",
  "userKeySecretName": "user_key",
  "deviceSecretName": "device",
  "sound": "",
  "criticalAsEmergency": false,
  "emergencyRetrySeconds": 60,
  "emergencyExpireSeconds": 3600
}
```

Messages are URL-encoded POST bodies sent only to `https://api.pushover.net/1/messages.json`; credentials never appear in the URL. Redirects, cookies, ambient proxies, and decompression are disabled, and every resolved address for the official host must be public. HTML, monospace formatting, supplementary URLs, callbacks, and attachments are deliberately omitted so agent-controlled content stays plain text and cannot create hidden links or remote loads.

AgentNotify maps low/normal/high priorities to Pushover -1/0/1. Critical is high priority by default. If the user explicitly enables emergency behavior, critical maps to priority 2 and includes a locally validated retry interval of at least 30 seconds and expiry of at most 10,800 seconds. Emergency alerts repeat through Pushover until acknowledged or expired. AgentNotify requires the initial API response to contain a receipt but does not yet poll, cancel, or persist receipt state.

The optional sound may be a documented built-in sound or a custom sound name uploaded to the application owner's Pushover account. Blank preserves the user's account default. Title and route-redacted body are bounded to Pushover's 250- and 1,024-Unicode-character limits without splitting surrogate pairs.

A 2xx response succeeds only when bounded JSON contains `status: 1`, a request identifier, and an emergency receipt when required. In accordance with Pushover's retry guidance, any 4xx—including the monthly-quota 429—is permanent because replaying the same request will not work. Network failures, 5xx, and malformed successes retry through the durable outbox. Pushover does not expose a message idempotency key, so an ambiguous timeout after acceptance can duplicate a notification. See the official [Pushover Message API](https://pushover.net/api).

## Planned adapters

The authoritative implementation order and constraints are in [FEATURE_BACKLOG.md](FEATURE_BACKLOG.md). Each adapter gets its own topic branch, contract tests, provider-specific retry classification, security notes, and user documentation before it is merged to `dev`.
