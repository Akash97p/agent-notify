import type { Metadata } from "next";
import Link from "next/link";
import { ArrowRight, BarChart3, BellRing, Gauge, Network, ShieldCheck, Terminal } from "lucide-react";

import { CopyCommand } from "@/components/copy-command";
import { ProductShot } from "@/components/product-shot";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { site, visuals } from "@/lib/site";

export const metadata: Metadata = {
  title: "Insights",
  description:
    "The AgentNotify dashboard: unresolved attention, local usage and estimated cost, live Codex and Claude quota, and router health in one local page.",
};

export default function InsightsPage() {
  return (
    <main>
      <section className="relative overflow-hidden border-b">
        <div className="grid-surface pointer-events-none absolute inset-0" />
        <div className="relative mx-auto max-w-7xl px-4 py-20 sm:px-6 sm:py-28">
          <div className="max-w-3xl">
            <Badge variant="secondary">Local dashboard · Insights</Badge>
            <h1 className="mt-5 text-5xl font-semibold leading-[1.02] tracking-[-0.05em] sm:text-6xl">See the work, the spend, and the runway.</h1>
            <p className="mt-6 text-lg leading-8 text-muted-foreground">Insights is one read-only page served by your own broker at <code className="rounded border bg-card px-1.5 py-0.5 font-mono text-base text-foreground">127.0.0.1</code>. It opens with what is waiting for you, then shows what your agents used, what those tokens would cost at published rates, how much quota each account has left, and whether routing is healthy.</p>
            <div className="mt-8 flex flex-wrap gap-3">
              <Button asChild size="lg"><Link href="/docs/web-ui/">Open the web interface guide <ArrowRight /></Link></Button>
              <Button asChild size="lg" variant="outline"><a href={site.releases}>Download release</a></Button>
            </div>
          </div>
          <div className="mt-14">
            <ProductShot src={visuals.insightsDashboard} alt="The AgentNotify Insights dashboard showing unresolved attention requests, live quota cards, a 30-day usage chart, agent mix, and router health" caption="Overview: unresolved attention first, then usage, live quota, routing, and broker health. Values shown are illustrative." priority />
          </div>
        </div>
      </section>

      <section className="mx-auto max-w-7xl px-4 py-20 sm:px-6">
        <div className="max-w-2xl">
          <Badge variant="outline">One page, four questions</Badge>
          <h2 className="mt-5 text-4xl font-semibold tracking-[-0.04em] sm:text-5xl">Not a wall of charts.</h2>
          <p className="mt-5 text-lg leading-8 text-muted-foreground">Each block answers a different question, keeps its own source, and stays quiet when there is nothing to report.</p>
        </div>
        <div className="mt-12 grid gap-4 md:grid-cols-2">
          <Feature icon={BellRing} title="Who is waiting for me?">Unresolved permission, input, blocked, and error requests, deduplicated by their logical key, with the newest state on top. Resolving one clears it everywhere.</Feature>
          <Feature icon={BarChart3} title="What did the agents use?">Read-only Claude Code, Codex, OpenCode, Kilo, Muse Code, and Gemini CLI records, grouped by day, project, model, provider, and recent session — including history from running WSL distributions on Windows.</Feature>
          <Feature icon={Gauge} title="How much is left?">Provider-reported Codex and Claude windows for the current profile, secondary profiles, WSL profiles, and up to sixteen named profiles you add. macOS keeps the lowest selected five-hour balance in the menu bar.</Feature>
          <Feature icon={Network} title="Is routing healthy?">Which upstream served each request, how many attempts it took, cooling targets, and a redacted per-request ledger. See the <Link className="underline underline-offset-4" href="/router/">model router</Link>.</Feature>
        </div>
      </section>

      <Separator />

      <section className="mx-auto grid max-w-7xl gap-12 px-4 py-20 sm:px-6 lg:grid-cols-2 lg:items-center">
        <div>
          <Badge variant="secondary">Provenance</Badge>
          <h2 className="mt-5 text-4xl font-semibold tracking-[-0.04em] sm:text-5xl">Usage and quota are never mixed.</h2>
          <p className="mt-5 leading-7 text-muted-foreground">Local usage is derived from the records your agents already write. Live quota is a provider snapshot fetched on demand and cached for a few minutes. The dashboard labels each one, shows when a value is stale, estimates Go plans from published caps rather than pretending to be a provider balance, and leaves a record unpriced when no published rate covers it.</p>
          <ul className="mt-7 grid gap-3 text-sm text-muted-foreground">
            <Row icon={ShieldCheck}>No prompt, response, or credential text ever enters a usage or routing report.</Row>
            <Row icon={ShieldCheck}>Account config stores profile labels and paths, never copied credentials.</Row>
            <Row icon={ShieldCheck}>The page is owner-only on loopback: foreign hosts and cross-site state changes are rejected.</Row>
            <Row icon={ShieldCheck}>Nothing is uploaded. There is no telemetry and no analytics service.</Row>
          </ul>
        </div>
        <Card className="bg-black">
          <CardHeader className="border-b">
            <div className="flex items-center gap-2 text-sm text-muted-foreground"><Terminal className="size-4" />terminal</div>
          </CardHeader>
          <CardContent className="relative p-5 font-mono text-[13px] leading-6 text-zinc-300">
            <CopyCommand value={"agentnotify ui"} />
            <pre className="overflow-x-auto"><code>{`$ agentnotify ui

# opens http://127.0.0.1:8787/ui/overview
#
#   Overview      attention · usage · quota · routing
#   Notifications full local history and filters
#   Questions     waiting interactions and answers
#   Usage         tokens, projects, sessions, cost
#   Live quota    provider windows per account
#   Routing       targets, combos, ledger, connectors
#
# the API stays on loopback behind the bearer token`}</code></pre>
          </CardContent>
        </Card>
      </section>

      <section className="border-y bg-card/40">
        <div className="mx-auto max-w-7xl px-4 py-20 sm:px-6">
          <div className="grid gap-10 lg:grid-cols-[.8fr_1.2fr] lg:items-start">
            <div>
              <Badge variant="outline">Getting there</Badge>
              <h2 className="mt-5 text-3xl font-semibold tracking-[-0.04em] sm:text-4xl">Install, then open the dashboard.</h2>
              <p className="mt-5 leading-7 text-muted-foreground">The web interface ships with the broker on every platform, so there is nothing extra to deploy.</p>
              <div className="mt-7 flex flex-wrap gap-3">
                <Button asChild><Link href="/docs/install-with-agent/">Install with an agent <ArrowRight /></Link></Button>
                <Button asChild variant="outline"><Link href="/docs/installation-unix/">macOS and Linux</Link></Button>
              </div>
            </div>
            <div className="grid gap-3">
              <Step index="1" title="Install AgentNotify">Windows setup, or the portable archive plus <code className="font-mono text-foreground">install.sh</code> on macOS and Linux.</Step>
              <Step index="2" title="Connect an agent">Give Codex or Claude Code the bundled skill, and optionally the auto-notify harness.</Step>
              <Step index="3" title="Open Insights">Run <code className="font-mono text-foreground">agentnotify ui</code> and the overview opens with attention, usage, quota, and routing.</Step>
            </div>
          </div>
        </div>
      </section>
    </main>
  );
}

function Feature({ icon: Icon, title, children }: { icon: typeof Gauge; title: string; children: React.ReactNode }) {
  return <Card><CardHeader><div className="mb-3 flex size-9 items-center justify-center rounded-md border bg-background"><Icon className="size-4" /></div><CardTitle>{title}</CardTitle><CardDescription className="leading-6">{children}</CardDescription></CardHeader></Card>;
}

function Row({ icon: Icon, children }: { icon: typeof ShieldCheck; children: React.ReactNode }) {
  return <li className="flex items-start gap-3"><Icon className="mt-0.5 size-4 shrink-0 text-foreground" /><span className="leading-6">{children}</span></li>;
}

function Step({ index, title, children }: { index: string; title: string; children: React.ReactNode }) {
  return (
    <Card className="gap-2 py-5">
      <CardHeader>
        <div className="flex items-center gap-3">
          <span className="flex size-6 items-center justify-center rounded-md border bg-background font-mono text-xs">{index}</span>
          <CardTitle className="text-base">{title}</CardTitle>
        </div>
        <CardDescription className="leading-6">{children}</CardDescription>
      </CardHeader>
    </Card>
  );
}
