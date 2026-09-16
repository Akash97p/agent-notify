# AgentNotify Relay

AgentNotify Relay is the hosted service that carries attention requests from your computers to your
phone. It runs at:

**`https://an.relay.dev.kabanitech.com`**

From AgentNotify's point of view Relay is one more opt-in outbound channel, selected in
**Settings → Channels → Providers** as the provider type **AgentNotify Relay**. Everything on this
page describes that channel. There is no server to install and no address to enter: the endpoint is
fixed, and connecting a computer is a browser approval.

---

## Why it exists

The other outbound channels hand your notifications to somebody else's product — Telegram, Slack,
an SMTP server, a push service. That works, but the message passes through an account and an
infrastructure you do not control.

Relay is the alternative: a hop built for this one job, that your computers send to and your phone
reads from. It is not a chat product with a notification feature bolted on, so the payload is sealed
per recipient device before it leaves your machine and the relay stores ciphertext it cannot read.
Local AgentNotify history stays authoritative either way — the relay is a transport, not a system of
record, and a relay that is unreachable never blocks or loses a local notification.

```
Coding agent → ARC → AgentNotify (local history, toast)
                          ↓ durable outbox
                     AgentNotify Relay          ← hosted
                          ↓ opaque push wake-up
                     AgentNotify mobile app
```

---

## What you need

| Piece | Status |
| --- | --- |
| The relay service | Hosted and running |
| The broker provider | Shipped on Windows, macOS, and Linux — pairing, sending, revocation |
| The operator console | Sign-in, pairing, and live delivery view in the browser |
| The mobile app | Android receiver and question-answer UI; owner phone round trips verified through 2026-09-13 |

---

## Connecting a computer

No tokens are typed anywhere. Pairing follows the OAuth device authorization grant (RFC 8628), the
same handshake used when signing a CLI or a smart TV into an account.

1. In **Settings → Channels → Providers**, add a provider and choose **AgentNotify Relay**.
2. Press **Connect**. AgentNotify shows a short code and opens your browser at the relay's approval
   page. If no browser can be opened — a headless server, an SSH session — it keeps polling and
   shows the URL and code so you can approve from any other device.
3. Sign in to the relay console and approve the code shown on the computer.
4. Press **Save provider**, then add a route so notifications actually flow.

The installation credential is delivered to the *waiting computer*, never to the browser, so there
is nothing to copy between the two. It is written straight into the platform-protected secret store
and is never displayed. Manual token entry still exists under **Advanced** for CI and scripted
installs.

## Connecting a phone

In the relay console, open **Phones → Add phone** and scan the QR code with the mobile app. The code
is a short-lived, single-use challenge rather than a credential; the phone's device credential is
minted only when it presents that challenge.

A phone belongs to your relay account rather than to one computer, so you scan once and every
computer you have paired can reach it. When a coding agent asks a question or awaits an approval,
an enabled Relay route with message-content consent can carry that interaction to the phone. The
running broker polls for the answer and returns it to a waiting supported host adapter. See
[Interactions](INTERACTIONS.md) and the [Relay/mobile answer contract](RELAY_INTERACTIONS.md).

---

## What the relay can and cannot see

Notification and interaction-request payloads are sealed per recipient device with X25519 + XChaCha20-Poly1305 before they
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
> notification delivery test in September 2026; the owner later verified phone-to-host answers
> for permission, choice, and text interactions on 2026-09-13. Confidentiality against
> the relay operator is implemented and tested; treat it as unreviewed rather than as an audited
> guarantee for the sealed request/notification direction. **V1 answers are plaintext to Relay**;
> they are authenticated, then revalidated by the broker for digest, nonce, expiry, and first-wins
> state. Sealing answers to the installation key remains future work.

Per-route, the **Include notification message off-device** switch controls whether the message body
leaves the machine at all. Leave it off for routes carrying anything you would not want stored
outside your computer, regardless of transport.

---

## Related pages

- [Outbound channels](CHANNELS.md) — every adapter, including the Relay provider's settings and
  security policy
- [Architecture](ARCHITECTURE.md) — where outbound delivery sits in the process model
- [ARC](ARC.md) — the attention request contract the relay transports
- [Relay interaction sync](RELAY_INTERACTIONS.md) — the phone answer path and its security limits
