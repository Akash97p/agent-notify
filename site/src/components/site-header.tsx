import Image from "next/image";
import Link from "next/link";
import { ArrowRight, GitBranch } from "lucide-react";

import { MobileNav } from "@/components/mobile-nav";
import { Button } from "@/components/ui/button";
import { basePath, navigation, site } from "@/lib/site";

export function SiteHeader() {
  return (
    <header className="sticky top-0 z-40 border-b bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/80">
      <div className="mx-auto flex h-14 max-w-7xl items-center px-4 sm:px-6">
        <Link href="/" className="flex items-center gap-2 font-semibold tracking-tight">
          <Image src={`${basePath}/an.png`} alt="" width={26} height={26} className="rounded-md" priority />
          AgentNotify
        </Link>
        <nav className="ml-8 hidden items-center gap-5 text-sm lg:flex" aria-label="Primary navigation">
          {navigation.map((item) => (
            <Link key={item.href} className="text-muted-foreground transition-colors hover:text-foreground" href={item.href}>
              {item.label}
            </Link>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-1">
          <Button asChild variant="ghost" size="sm" className="hidden sm:inline-flex">
            <a href={site.repository}><GitBranch />GitHub</a>
          </Button>
          <Button asChild size="sm">
            <a href={site.releases}>Download <ArrowRight /></a>
          </Button>
          <MobileNav />
        </div>
      </div>
    </header>
  );
}
