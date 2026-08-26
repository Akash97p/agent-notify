export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? "/agent-notify";

export const site = {
  name: "AgentNotify",
  description: "The local human-attention broker for coding agents.",
  url: "https://akash97p.github.io/agent-notify",
  repository: "https://github.com/Akash97p/agent-notify",
  releases: "https://github.com/Akash97p/agent-notify/releases",
} as const;
