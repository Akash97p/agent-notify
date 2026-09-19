import Link from "next/link";

import { Button } from "@/components/ui/button";
import { docsEntry } from "@/lib/site";

export default function NotFound() {
  return (
    <main className="mx-auto flex min-h-[60vh] max-w-3xl flex-col items-start justify-center px-4 sm:px-6">
      <p className="font-mono text-sm text-muted-foreground">404</p>
      <h1 className="mt-4 text-4xl font-semibold tracking-tight">Page not found.</h1>
      <p className="mt-4 text-muted-foreground">That page does not exist. Installation starts with an agent, or you can browse the guides from any documentation page.</p>
      <div className="mt-8 flex flex-wrap gap-3">
        <Button asChild><Link href={docsEntry}>Install with an agent</Link></Button>
        <Button asChild variant="outline"><Link href="/">Back to the overview</Link></Button>
      </div>
    </main>
  );
}
