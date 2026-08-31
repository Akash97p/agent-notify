# AgentNotify Relay

AgentNotify Relay is a separate, self-hostable service that carries attention requests from your
computers to your phone. It is open source and lives in its own repository:

**[github.com/Akash97p/agent-notify-relay](https://github.com/Akash97p/agent-notify-relay)**

From AgentNotify's point of view Relay is one more opt-in outbound channel, selected in
**Settings → Channels → Providers** as the provider type **AgentNotify Relay**. Everything on this
page describes that channel; the relay's own setup, operator console, and API are documented in the
relay repository.

---

## Why it exists

The other outbound channels hand your notifications to somebody else's product — Telegram, Slack,
an SMTP server, a push service. That works, but the message passes through an account and an
infrastructure you do not control.

Relay is the alternative for people who would rather run the hop themselves: a small service on a
VPS or home server that your computers send to and your phone reads from. Local AgentNotify history
stays authoritative either way — the relay is a transport, not a system of record, and a relay that
is unreachable never blocks or loses a local notification.

```
Coding agent → ARC → AgentNotify (local history, toast)
                          ↓ durable outbox
                     AgentNotify Relay          ← you host this
                          ↓ opaque push wake-up
                     AgentNotify mobile app
```

---

## What you need

| Piece | Status |
| --- | --- |
| The relay service | Open source, self-hostable today |
| The desktop provider | Shipped in AgentNotify — pairing, sending, revocation |
| The operator console | Shipped with the relay — sign-in, pairing, live delivery view |
| The mobile app | **Not built yet.** Flutter client is planned |
| Relay Go (hosted) | **Not available yet.** Visible in the UI as *coming soon* |

Until the mobile app exists you can still run the whole chain end to end — the relay repository
ships `scripts/dummy-device.ts`, a command-line stand-in that pairs, receives, decrypts, and
acknowledges exactly as a phone will.

---

## Connecting a computer

No tokens are typed anywhere. Pairing follows the OAuth device authorization grant (RFC 8628), the
same handshake used when signing a CLI or a smart TV into an account.

1. In **Settings → Channels → Providers**, add a provider and choose **AgentNotify Relay**.
2. Choose **Custom — self-hosted** and enter your relay's base URL.
   For a relay on the same machine, also tick **Allow private/loopback destinations**.
3. Press **Connect**. AgentNotify shows a short code and opens your browser at the relay's approval
   page. If no browser can be opened — a headless server, an SSH session — it keeps polling and
   shows the URL and code so you can approve from any other device.
4. Sign in to the relay console and approve the code shown on the computer.
5. Press **Save provider**, then add a route so notifications actually flow.

The installation credential is delivered to the *waiting computer*, never to the browser, so there
is nothing to copy between the two. It is written straight into the platform-protected secret store
and is never displayed. Manual token entry still exists under **Advanced** for CI and scripted
installs.

## Connecting a phone

In the relay console, open **Phones → Add phone** and scan the QR code with the mobile app. The code
is a short-lived, single-use challenge rather than a credential; the phone's device credential is
minted only when it presents that challenge.

A phone belongs to your relay account rather than to one computer, so you scan once and every
computer you have paired can reach it.

---

## What the relay can and cannot see

Notification payloads are sealed per recipient device with X25519 + XChaCha20-Poly1305 before they
leave your machine, and the relay stores only the sealed bytes. It is not a decryption client, and
its operator console renders delivery metadata only — never message content.

The relay does see, and needs to see, routing metadata: which installation sent an envelope, which
device it is for, the key id, timestamps, sizes, and delivery state. Your sender name is visible if
you set one. Push wake-ups carry an envelope id and nothing else, so the push provider never
receives content.

> **Current limitation.** The desktop adapter still emits a placeholder transport rather than a real
> sealed box, so today's envelopes are not yet genuinely encrypted end to end. The envelope format,
> key exchange, and device key registration are implemented and verified on the relay side; the
> desktop encryption is the remaining piece. Treat end-to-end confidentiality as *not yet delivered*
> until this page says otherwise.

Per-route, the **Include notification message off-device** switch controls whether the message body
leaves the machine at all. Leave it off for routes carrying anything you would not want stored
outside your computer, regardless of transport.

---

## Running your own relay

Full instructions, configuration reference, and the API contract live in the relay repository. The
short version:

```bash
git clone https://github.com/Akash97p/agent-notify-relay
cd agent-notify-relay
bun install

RELAY_PORT=4000 RELAY_PUBLIC_URL=http://localhost:4000 \
RELAY_ENCRYPTION_KEY=$(openssl rand -base64 32) \
RELAY_ADMIN_EMAIL=you@example.com RELAY_ADMIN_PASSWORD=a-long-passphrase \
RELAY_DATABASE_URL=sqlite:///data/relay.db RELAY_FCM_STUB=true \
bun run apps/api/src/index.ts
```

The console is then at `http://localhost:4000`. SQLite is the default for a single instance;
PostgreSQL is supported for multi-instance deployments. A Dockerfile and Compose file are included.

For anything internet-facing, put it behind TLS and set a real `RELAY_ENCRYPTION_KEY` — the relay
refuses to start in production without a configured operator account, and rejects the development
placeholder key.

---

## Related pages

- [Outbound channels](channels) — every adapter, including the Relay provider's settings and
  security policy
- [Architecture](architecture) — where outbound delivery sits in the process model
- [ARC](arc) — the attention request contract the relay transports
