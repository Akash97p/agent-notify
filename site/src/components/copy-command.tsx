"use client";

import { useState } from "react";
import { Check, Copy } from "lucide-react";

import { Button } from "@/components/ui/button";

export function CopyCommand({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    await navigator.clipboard.writeText(value);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1600);
  }

  return (
    <Button onClick={copy} variant="ghost" size="icon" className="absolute right-2 top-2 text-muted-foreground" aria-label="Copy command">
      {copied ? <Check /> : <Copy />}
    </Button>
  );
}
