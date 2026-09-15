export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? "/agent-notify";

export const site = {
  name: "AgentNotify",
  description: "A local human-attention broker for coding agents, with a web dashboard for usage, estimated cost, and live account quota.",
  url: "https://akash97p.github.io/agent-notify",
  repository: "https://github.com/Akash97p/agent-notify",
  relayRepository: "https://github.com/Akash97p/agent-notify-relay",
  releases: "https://github.com/Akash97p/agent-notify/releases",
} as const;
