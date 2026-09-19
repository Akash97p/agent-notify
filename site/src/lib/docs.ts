import fs from "node:fs";
import path from "node:path";
import { unified } from "unified";
import remarkParse from "remark-parse";
import remarkGfm from "remark-gfm";
import remarkRehype from "remark-rehype";
import rehypeSlug from "rehype-slug";
import rehypeStringify from "rehype-stringify";
import { visit } from "unist-util-visit";
import type { Element, Root } from "hast";

import { basePath } from "@/lib/site";

export type DocDefinition = {
  source: string;
  slug: string;
  title: string;
  section: string;
  description: string;
};

export const docs: DocDefinition[] = [
  { source: "docs/INSTALL_WITH_AGENT.md", slug: "install-with-agent", title: "Install with an agent", section: "Getting started", description: "Let a terminal-capable coding agent install and verify AgentNotify." },
  { source: "docs/INSTALLATION.md", slug: "installation", title: "Install on Windows", section: "Getting started", description: "Install the Windows desktop application and CLI." },
  { source: "docs/INSTALLATION_UNIX.md", slug: "installation-unix", title: "Install on macOS and Linux", section: "Getting started", description: "Run the portable broker and the native macOS quota menu bar." },
  { source: "docs/TROUBLESHOOTING.md", slug: "troubleshooting", title: "Troubleshooting", section: "Getting started", description: "Diagnose common installation, API, and delivery problems." },
  { source: "docs/WEB_UI.md", slug: "web-ui", title: "Web interface", section: "Using AgentNotify", description: "Questions, settings, usage, live quota, and macOS menu-bar configuration." },
  { source: "docs/CLI.md", slug: "cli", title: "Command line", section: "Using AgentNotify", description: "Commands, flags, output shapes, and exit codes." },
  { source: "docs/API.md", slug: "api", title: "Local REST API", section: "Using AgentNotify", description: "Authenticated loopback endpoints and request contracts." },
  { source: "docs/ARC.md", slug: "arc", title: "Attention Request Contract", section: "Using AgentNotify", description: "ARC 0.2 lifecycle, schemas, and reference binding." },
  { source: "docs/ROUTER.md", slug: "router", title: "Provider router", section: "Using AgentNotify", description: "Opt-in model routing, translation, switching, failover, and security boundaries." },
  { source: "docs/CONFIGURATION.md", slug: "configuration", title: "Configuration", section: "Using AgentNotify", description: "Configuration files, defaults, and notification types." },
  { source: "docs/CHANNELS.md", slug: "channels", title: "Outbound channels", section: "Using AgentNotify", description: "Nineteen opt-in adapters and their security policies." },
  { source: "docs/RELAY.md", slug: "relay", title: "AgentNotify Relay", section: "Using AgentNotify", description: "The hosted transport from your computers to your phone." },
  { source: "docs/AGENT_INTEGRATION.md", slug: "agent-integration", title: "Agent integration", section: "Agents", description: "When and how an agent should request attention." },
  { source: "docs/AGENT_SKILLS.md", slug: "agent-skills", title: "Agent skills", section: "Agents", description: "Install the bundled skill into Codex or Claude Code." },
  { source: "docs/HARNESS.md", slug: "harness", title: "Agent harnesses", section: "Agents", description: "Auto-notify harnesses for eleven coding hosts." },
  { source: "docs/INTERACTIONS.md", slug: "interactions", title: "Interactions", section: "Agents", description: "Waiting questions and permissions: model, API, and CLI." },
  { source: "docs/RELAY_INTERACTIONS.md", slug: "relay-interactions", title: "Relay interaction sync", section: "Agents", description: "Relay/mobile wire contract for answering from the phone." },
  { source: "docs/BIDIRECTIONAL_AGENT_COMMUNICATION.md", slug: "bidirectional-agent-communication", title: "Bidirectional agent communication", section: "Agents", description: "Shipped answer paths, remaining work, host adapters, and protocol research." },
  { source: "docs/ARCHITECTURE.md", slug: "architecture", title: "Architecture", section: "Project", description: "Process model, components, persistence, and failure behavior." },
  { source: "SECURITY.md", slug: "security", title: "Security policy", section: "Project", description: "Trust boundaries, secret handling, provider risks, and vulnerability reporting." },
  { source: "docs/CROSS_PLATFORM.md", slug: "cross-platform", title: "Cross-platform plan", section: "Project", description: "Portable boundaries, the macOS quota status item, and remaining native-client work." },
  { source: "docs/ROADMAP.md", slug: "roadmap", title: "Roadmap", section: "Project", description: "Current direction and explicitly uncommitted work." },
  { source: "docs/FEATURE_BACKLOG.md", slug: "feature-backlog", title: "Feature backlog", section: "Project", description: "Ordered capabilities and implementation status." },
  { source: "docs/RELEASING.md", slug: "releasing", title: "Releasing", section: "Project", description: "Versions, tags, artifacts, and release workflows." },
  { source: "docs/VERIFICATION.md", slug: "verification", title: "Verification record", section: "Project", description: "What has and has not actually been verified." },
  { source: "docs/BUG.md", slug: "bugs", title: "Bug log", section: "Project", description: "Defects discovered after completion and their causes." },
];

export type RenderedDoc = DocDefinition & {
  html: string;
  toc: { level: number; id: string; text: string }[];
  sourceUrl: string;
};

const repoRoot = path.resolve(process.cwd(), "..");
const sourceToSlug = new Map(docs.map((doc) => [doc.source, doc.slug]));

function rewriteLinks(sourcePath: string) {
  return () => (tree: Root) => {
    visit(tree, "element", (node: Element) => {
      if (node.tagName !== "a" || typeof node.properties?.href !== "string") return;
      const href = node.properties.href;
      if (/^(https?:|mailto:|#)/.test(href)) {
        if (/^https?:/.test(href)) {
          node.properties.target = "_blank";
          node.properties.rel = ["noreferrer"];
        }
        return;
      }

      const [rawPath, fragment] = href.split("#", 2);
      const normalized = path.posix.normalize(path.posix.join(path.posix.dirname(sourcePath), rawPath));
      const slug = sourceToSlug.get(normalized);
      node.properties.href = slug
        ? `${basePath}/docs/${slug}/${fragment ? `#${fragment}` : ""}`
        : `https://github.com/Akash97p/agent-notify/blob/main/${normalized}${fragment ? `#${fragment}` : ""}`;
    });
  };
}

function plainText(value: string) {
  return value
    .replace(/<code>(.*?)<\/code>/g, "$1")
    .replace(/<[^>]+>/g, "")
    .replaceAll("&amp;", "&")
    .replaceAll("&lt;", "<")
    .replaceAll("&gt;", ">");
}

export async function getDoc(slug: string): Promise<RenderedDoc | null> {
  const definition = docs.find((doc) => doc.slug === slug);
  if (!definition) return null;

  const markdown = fs.readFileSync(path.join(repoRoot, definition.source), "utf8");
  const rendered = await unified()
    .use(remarkParse)
    .use(remarkGfm)
    .use(remarkRehype)
    .use(rehypeSlug)
    .use(rewriteLinks(definition.source))
    .use(rehypeStringify)
    .process(markdown);
  const html = String(rendered);
  const toc = Array.from(html.matchAll(/<h([23]) id="([^"]+)">([\s\S]*?)<\/h\1>/g)).map((match) => ({
    level: Number(match[1]),
    id: match[2],
    text: plainText(match[3]),
  }));

  return {
    ...definition,
    html,
    toc,
    sourceUrl: `https://github.com/Akash97p/agent-notify/blob/main/${definition.source}`,
  };
}

export function docsBySection() {
  return docs.reduce<Record<string, DocDefinition[]>>((groups, doc) => {
    (groups[doc.section] ??= []).push(doc);
    return groups;
  }, {});
}
