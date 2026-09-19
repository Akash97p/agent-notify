"use client";

import Link from "next/link";
import { Menu } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Sheet, SheetClose, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from "@/components/ui/sheet";

const links = [
  ["Documentation", "/docs/"],
  ["ARC", "/docs/arc/"],
  ["Router", "/docs/router/"],
  ["Channels", "/docs/channels/"],
  ["Relay", "/docs/relay/"],
  ["Architecture", "/docs/architecture/"],
] as const;

export function MobileNav() {
  return (
    <Sheet>
      <SheetTrigger asChild>
        <Button variant="ghost" size="icon" className="lg:hidden" aria-label="Open navigation">
          <Menu />
        </Button>
      </SheetTrigger>
      <SheetContent className="p-0">
        <SheetHeader className="border-b px-6 py-5 text-left">
          <SheetTitle>AgentNotify</SheetTitle>
          <SheetDescription>Human attention infrastructure for coding agents.</SheetDescription>
        </SheetHeader>
        <nav className="grid gap-1 p-4" aria-label="Mobile navigation">
          {links.map(([label, href]) => (
            <SheetClose asChild key={href}>
              <Link className="rounded-md px-3 py-2.5 text-sm text-muted-foreground hover:bg-accent hover:text-foreground" href={href}>{label}</Link>
            </SheetClose>
          ))}
          <SheetClose asChild>
            <a className="rounded-md px-3 py-2.5 text-sm text-muted-foreground hover:bg-accent hover:text-foreground" href="https://github.com/Akash97p/agent-notify">GitHub</a>
          </SheetClose>
        </nav>
      </SheetContent>
    </Sheet>
  );
}
