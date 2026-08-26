import Link from "next/link";

import { Button } from "@/components/ui/button";

export default function NotFound() {
  return <main className="mx-auto flex min-h-[60vh] max-w-3xl flex-col items-start justify-center px-4 sm:px-6"><p className="font-mono text-sm text-muted-foreground">404</p><h1 className="mt-4 text-4xl font-semibold tracking-tight">Page not found.</h1><p className="mt-4 text-muted-foreground">The requested documentation page does not exist.</p><Button asChild className="mt-8"><Link href="/docs/">Browse documentation</Link></Button></main>;
}
