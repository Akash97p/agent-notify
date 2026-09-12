import Link from "next/link";
import {
  ArrowRight,
  BellRing,
  Check,
  Database,
  GitBranch,
  LockKeyhole,
  Network,
  Radio,
  Route,
  Terminal,
} from "lucide-react";

import { CopyCommand } from "@/components/copy-command";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { site } from "@/lib/site";

const channels = ["Desktop", "SMTP", "Telegram", "Discord", "Slack", "Teams", "Zoho Cliq", "Google Chat", "Mattermost", "Matrix", "ntfy", "Gotify", "Pushover", "Pushbullet", "Twilio", "WhatsApp", "MQTT", "Webhook"];

export default function Home() {
  return (
    <main>
      <section className="relative overflow-hidden border-b">
        <div className="grid-surface pointer-events-none absolute inset-0" />
        <div className="relative mx-auto grid max-w-7xl gap-12 px-4 py-24 sm:px-6 sm:py-32 lg:grid-cols-[1.15fr_.85fr] lg:items-center lg:py-40">
          <div>
            <Badge variant="outline" className="mb-6 border-border bg-background/70 px-3 py-1 text-muted-foreground">Open source · Local first · No telemetry</Badge>
            <h1 className="max-w-4xl text-5xl font-semibold leading-[0.98] tracking-[-0.055em] sm:text-7xl">Know exactly when your agents need you.</h1>
            <p className="mt-7 max-w-2xl text-lg leading-8 text-muted-foreground sm:text-xl">AgentNotify turns permissions, questions, blockers, failures, and completions into durable human-attention requests—then routes them to the surfaces you already use, and carries your answer back to the agent that is waiting.</p>
            <div className="mt-9 flex flex-wrap gap-3">
              <Button asChild size="lg"><a href={site.releases}>Download latest release <ArrowRight /></a></Button>
              <Button asChild size="lg" variant="outline"><Link href="/docs/">Read the documentation</Link></Button>
            </div>
            <p className="mt-5 text-sm text-muted-foreground">Windows has the full graphical app. macOS and Linux currently run the CLI and headless broker.</p>
          </div>

          <Card className="overflow-hidden bg-card/90 shadow-2xl shadow-black">
            <CardHeader className="border-b">
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2 text-sm text-muted-foreground"><Terminal className="size-4" />agentnotify</div>
                <Badge variant="secondary"><span className="size-1.5 rounded-full bg-white" />broker online</Badge>
              </div>
            </CardHeader>
            <CardContent className="p-0">
              <div className="relative bg-black p-5 font-mono text-[13px] leading-6 text-zinc-300">
                <CopyCommand value={'agentnotify send --type permission_required --title "Deploy approval" --message "May I publish the release?"'} />
                <span className="text-zinc-600">$ </span>agentnotify send \<br />
                &nbsp;&nbsp;--type permission_required \<br />
                &nbsp;&nbsp;--title <span className="text-white">&quot;Deploy approval&quot;</span> \<br />
                &nbsp;&nbsp;--message <span className="text-white">&quot;May I publish the release?&quot;</span>
              </div>
              <div className="grid gap-3 p-5">
                <div className="flex items-start gap-3 rounded-lg border bg-background p-4">
                  <BellRing className="mt-0.5 size-4" />
                  <div><p className="text-sm font-medium">Deploy approval</p><p className="mt-1 text-sm text-muted-foreground">Codex needs permission · release</p></div>
                  <Badge variant="outline" className="ml-auto">high</Badge>
                </div>
                <div className="flex items-center gap-2 text-xs text-muted-foreground"><Check className="size-3.5" />Persisted locally before optional delivery</div>
              </div>
            </CardContent>
          </Card>
        </div>
      </section>

      <section className="mx-auto max-w-7xl px-4 py-10 sm:px-6">
        <div className="grid divide-y rounded-xl border bg-card sm:grid-cols-4 sm:divide-x sm:divide-y-0">
          {[['835', 'automated tests'], ['19', 'outbound adapters'], ['3', 'supported OS families'], ['0', 'telemetry services']].map(([value, label]) => (
            <div className="px-6 py-5" key={label}><p className="text-2xl font-semibold tracking-tight">{value}</p><p className="text-sm text-muted-foreground">{label}</p></div>
          ))}
        </div>
      </section>

      <section className="mx-auto max-w-7xl px-4 py-24 sm:px-6">
        <div className="max-w-2xl"><Badge variant="secondary">Why AgentNotify</Badge><h2 className="mt-5 text-4xl font-semibold tracking-[-0.04em] sm:text-5xl">A broker for the human bottleneck.</h2><p className="mt-5 text-lg leading-8 text-muted-foreground">Agents can work for hours without help. The costly moment is when one silently stops at a permission prompt, missing credential, or decision only you can make.</p></div>
        <div className="mt-12 grid gap-4 md:grid-cols-3">
          <Feature icon={Database} title="Local source of truth">SQLite keeps unresolved state, deduplication keys, history, and delivery attempts on your machine.</Feature>
          <Feature icon={Route} title="Route after persistence">Desktop delivery is authoritative. Optional remote channels run later through a durable outbox.</Feature>
          <Feature icon={LockKeyhole} title="Explicit trust boundary">The API stays on loopback behind a random bearer token. External channels are disabled until configured.</Feature>
        </div>
      </section>

      <Separator />

      <section className="mx-auto grid max-w-7xl gap-12 px-4 py-24 sm:px-6 lg:grid-cols-2 lg:items-center">
        <div>
          <Badge variant="secondary">Two-way</Badge>
          <h2 className="mt-5 text-4xl font-semibold tracking-[-0.04em] sm:text-5xl">Answer it, don&apos;t just read it.</h2>
          <p className="mt-5 text-lg leading-8 text-muted-foreground">An agent can ask you something and carry on with other work while you decide. Your answer is waiting when it checks back, and it reaches the session that asked.</p>
          <p className="mt-5 leading-7 text-muted-foreground">For Codex and Claude Code, the host&apos;s own approval prompt can wait for you: every shell command and file write pauses with the exact command shown. If nothing answers in time it falls back to the ordinary local prompt, so it can never lock you out.</p>
          <div className="mt-8 flex flex-wrap gap-3"><Button asChild><Link href="/docs/interactions/">How interactions work <ArrowRight /></Link></Button><Button asChild variant="outline"><Link href="/docs/harness/">Agent harnesses</Link></Button></div>
        </div>
        <Card className="bg-black">
          <CardContent className="p-5 font-mono text-[13px] leading-6 text-zinc-300">
            <pre className="overflow-x-auto"><code>{`$ agentnotify install-harness claude --ask

# Claude Code now pauses for your decision:
#   "Claude Code approval: Bash rm -rf /tmp/build"
#     [ Allow once ]  [ Deny ]

# and the session receives exactly what you chose
{"hookSpecificOutput": {
  "hookEventName": "PermissionRequest",
  "decision": { "behavior": "deny" }
}}`}</code></pre>
          </CardContent>
        </Card>
      </section>

      <Separator />

      <section className="mx-auto grid max-w-7xl gap-12 px-4 py-24 sm:px-6 lg:grid-cols-2 lg:items-center">
        <div>
          <Badge variant="secondary">ARC 0.1</Badge>
          <h2 className="mt-5 text-4xl font-semibold tracking-[-0.04em] sm:text-5xl">One contract for attention.</h2>
          <p className="mt-5 text-lg leading-8 text-muted-foreground">The Attention Request Contract is an open JSON contract for creating, updating, and resolving bounded requests for human attention. It separates an immutable event from the condition that remains unresolved.</p>
          <div className="mt-8 flex flex-wrap gap-3"><Button asChild><Link href="/docs/arc/">Read the specification <ArrowRight /></Link></Button><Button asChild variant="outline"><a href={`${site.url}/schemas/arc-0.1.schema.json`}>JSON Schema</a></Button></div>
        </div>
        <Card className="bg-black">
          <CardContent className="p-5 font-mono text-[13px] leading-6 text-zinc-300">
            <pre className="overflow-x-auto"><code>{`{
  "arc_version": "0.1",
  "event_id": "evt_release_42",
  "event_type": "request.created",
  "occurred_at": "2026-08-26T01:15:00Z",
  "sender": { "id": "codex", "name": "Codex" },
  "request": {
    "key": "release-approval",
    "kind": "permission",
    "message": "May I publish the release?",
    "priority": "high"
  }
}`}</code></pre>
          </CardContent>
        </Card>
      </section>

      <section className="border-y bg-card/40">
        <div className="mx-auto max-w-7xl px-4 py-24 sm:px-6">
          <div className="grid gap-10 lg:grid-cols-[.75fr_1.25fr]">
            <div><Badge variant="outline">Delivery</Badge><h2 className="mt-5 text-4xl font-semibold tracking-[-0.04em]">Your attention, where you want it.</h2><p className="mt-5 leading-7 text-muted-foreground">Every remote destination is opt-in. Credentials are encrypted at rest, payloads are bounded, and provider failures never reject the local request.</p><Button asChild variant="outline" className="mt-7"><Link href="/docs/channels/">Explore channels <ArrowRight /></Link></Button></div>
            <div className="flex content-start flex-wrap gap-2">{channels.map((channel) => <Badge key={channel} variant="secondary" className="px-3 py-1.5 text-sm">{channel}</Badge>)}</div>
          </div>
        </div>
      </section>

      <section className="mx-auto max-w-7xl px-4 py-24 sm:px-6">
        <div className="text-center"><Badge variant="secondary">Architecture</Badge><h2 className="mt-5 text-4xl font-semibold tracking-[-0.04em]">Simple at the boundary. Durable underneath.</h2></div>
        <div className="mt-12 grid gap-3 lg:grid-cols-[1fr_auto_1fr_auto_1fr_auto_1fr] lg:items-center">
          <Flow icon={Terminal} title="Coding agents" detail="CLI · ARC · future adapters" /><ArrowRight className="mx-auto hidden text-muted-foreground lg:block" />
          <Flow icon={Radio} title="Loopback API" detail="Bearer auth · validation" /><ArrowRight className="mx-auto hidden text-muted-foreground lg:block" />
          <Flow icon={Database} title="Local lifecycle" detail="SQLite · dedup · history" /><ArrowRight className="mx-auto hidden text-muted-foreground lg:block" />
          <Flow icon={Network} title="Attention routes" detail="Desktop · chat · mail · push" />
        </div>
      </section>

      <section className="mx-auto max-w-7xl px-4 py-12 sm:px-6">
        <Card className="items-center bg-primary py-12 text-center text-primary-foreground">
          <CardHeader className="max-w-3xl"><CardTitle className="text-3xl tracking-[-0.035em] sm:text-4xl">Stop checking every terminal.</CardTitle><CardDescription className="mt-3 text-base text-primary-foreground/70">Install AgentNotify, give your coding agent the bundled skill, and let the broker tell you when human attention is actually required.</CardDescription></CardHeader>
          <CardContent className="flex flex-wrap justify-center gap-3"><Button asChild variant="secondary"><a href={site.releases}>Download release <ArrowRight /></a></Button><Button asChild variant="outline" className="border-primary-foreground/25 bg-transparent text-primary-foreground hover:bg-primary-foreground/10"><a href={site.repository}><GitBranch />View source</a></Button></CardContent>
        </Card>
      </section>
    </main>
  );
}

function Feature({ icon: Icon, title, children }: { icon: typeof Database; title: string; children: React.ReactNode }) {
  return <Card><CardHeader><div className="mb-3 flex size-9 items-center justify-center rounded-md border bg-background"><Icon className="size-4" /></div><CardTitle>{title}</CardTitle><CardDescription className="leading-6">{children}</CardDescription></CardHeader></Card>;
}

function Flow({ icon: Icon, title, detail }: { icon: typeof Terminal; title: string; detail: string }) {
  return <Card className="gap-3 py-5"><CardHeader><Icon className="mb-3 size-5" /><CardTitle className="text-base">{title}</CardTitle><CardDescription>{detail}</CardDescription></CardHeader></Card>;
}
