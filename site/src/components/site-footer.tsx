import Link from "next/link";

import { Separator } from "@/components/ui/separator";
import { docsEntry, site } from "@/lib/site";

export function SiteFooter() {
  return (
    <footer className="mx-auto max-w-7xl px-4 pb-10 pt-16 sm:px-6">
      <Separator />
      <div className="flex flex-col gap-4 pt-6 text-sm text-muted-foreground sm:flex-row sm:items-center sm:justify-between">
        <p>AgentNotify · Kabani Tech Private Limited · MIT License</p>
        <nav className="flex flex-wrap gap-5" aria-label="Footer navigation">
          <Link href={docsEntry}>Docs</Link>
          <Link href="/insights/">Insights</Link>
          <Link href="/router/">Model router</Link>
          <Link href="/docs/arc/">ARC</Link>
          <Link href="/docs/relay/">Relay</Link>
          <a href={site.repository}>Source</a>
        </nav>
      </div>
    </footer>
  );
}
