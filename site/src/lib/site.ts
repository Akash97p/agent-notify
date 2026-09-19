export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? "/agent-notify";

export const site = {
  name: "AgentNotify",
  description:
    "A local human-attention broker and dashboard for coding agents: notifications, questions, usage, estimated cost, live account quota, and an opt-in model router.",
  url: "https://akash97p.github.io/agent-notify",
  repository: "https://github.com/Akash97p/agent-notify",
  releases: "https://github.com/Akash97p/agent-notify/releases",
} as const;

/** Documentation entry point. There is no separate documentation landing page. */
export const docsEntry = "/docs/install-with-agent/";

export const navigation = [
  { label: "Documentation", href: docsEntry },
  { label: "Insights", href: "/insights/" },
  { label: "Model router", href: "/router/" },
  { label: "ARC", href: "/docs/arc/" },
  { label: "Channels", href: "/docs/channels/" },
  { label: "Relay", href: "/docs/relay/" },
] as const;

export const visuals = {
  attentionQueue: `${basePath}/visuals/attention-queue.svg`,
  insightsDashboard: `${basePath}/visuals/insights-dashboard.svg`,
  routerFlow: `${basePath}/visuals/router-flow.svg`,
} as const;
