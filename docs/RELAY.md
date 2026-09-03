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
| The mobile app | Android Expo/TypeScript receiver implemented in [`agent-notify-relay-mobile`](https://github.com/Akash97p/agent-notify-relay-mobile); owner-reported live flow working on 2026-09-03 |
| Relay Go (hosted) | **Not available yet.** Visible in the UI as *coming soon* |

The relay repository also retains `scripts/dummy-device.ts`, a command-line stand-in for contract
and deployment checks without an Android device.

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

Sealing is implemented on both sides and verified against shared test vectors: the .NET
adapter reproduces the relay's TypeScript output byte for byte, and an envelope sealed on the
desktop decrypts correctly with the relay's own implementation and fails authentication if any
bound field is altered. A device that has not registered a public key is skipped rather than
sent in the clear.

> **Scope of the claim.** The envelope format has not had an independent cryptographic review,
> and the mobile implementation is Android-first. The owner reports a successful live end-to-end
> device test on 2026-09-03; this documentation branch did not repeat it. Confidentiality against
> the relay operator is implemented and tested; treat it as unreviewed rather than as an audited
> guarantee.

Per-route, the **Include notification message off-device** switch controls whether the message body
leaves the machine at all. Leave it off for routes carrying anything you would not want stored
outside your computer, regardless of transport.

---

## Running your own relay

A prebuilt container image is published on every release, so you do not need to clone the
repository or install anything to build it. Save this as `compose.yaml`, change the four values at
the top, and run `docker compose up -d`:

```yaml
services:
  relay:
    image: ghcr.io/akash97p/agent-notify-relay:latest
    restart: unless-stopped
    ports:
      # Change the left number to publish on a different port.
      - "4000:8787"
    environment:
      # --- change these four ---
      RELAY_ADMIN_EMAIL: you@example.com
      RELAY_ADMIN_PASSWORD: change-me-to-a-long-passphrase
      # The address AgentNotify and your phone will reach this relay at.
      # Must match the published port above, and be https:// once it is not local.
      RELAY_PUBLIC_URL: http://localhost:4000
      # 32 random bytes. Generate with: openssl rand -base64 32
      RELAY_ENCRYPTION_KEY: replace-with-openssl-rand-base64-32
      # --- sensible defaults ---
      RELAY_DATABASE_URL: sqlite:///data/relay.db
      # Leave true until you have configured Firebase; deliveries still progress.
      RELAY_FCM_STUB: "true"
    volumes:
      - relay-data:/data

volumes:
  relay-data:
```

Open `http://localhost:4000` and sign in with the email and password you set. That is the address
you then give AgentNotify when you press **Connect**.

The password must be at least 12 characters — the relay refuses to start in production without an
operator account, and rejects the development encryption key. `RELAY_ENCRYPTION_KEY` protects push
tokens at rest, so losing it means re-pairing every phone. Put anything internet-facing behind TLS
and set `RELAY_PUBLIC_URL` to the `https://` address, since that value is what the desktop
validates and what your phone scans.

SQLite is the default and is right for a single instance; PostgreSQL is supported for
multi-instance deployments. Running from source, the configuration reference, and the full API
contract are documented in the
[relay repository](https://github.com/Akash97p/agent-notify-relay).

---

## Related pages

- [Outbound channels](channels) — every adapter, including the Relay provider's settings and
  security policy
- [Architecture](architecture) — where outbound delivery sits in the process model
- [ARC](arc) — the attention request contract the relay transports
