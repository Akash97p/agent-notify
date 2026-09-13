import { api } from "../api.js";
import { h, mount, icon, clear, card, pageHead, badge, notice, duration, button } from "../dom.js";

function stat(label, iconName, value, sub, href) {
  return h(href ? "a" : "div", { class: "card stat", href },
    h("span", { class: "stat-label" }, icon(iconName), label),
    h("span", { class: "stat-value", text: String(value) }),
    h("span", { class: "stat-sub", text: sub }));
}

export default {
  async render(page, ctx) {
    const draw = (o) => {
      const d = o.delivery;
      const stuck = d.dead_letter > 0;
      mount(page, 
        pageHead("Overview", "The broker on this computer: what is waiting on you, where notifications go, and how it is running.",
          button("Refresh", { iconName: "refresh", onClick: async () => draw(await ctx.refreshOverview()) })),

        h("div", { class: "stats" },
          stat("Waiting on you", "bell", o.counts.active_notifications,
            o.counts.active_notifications === 1 ? "unresolved notification" : "unresolved notifications", "#/attention"),
          stat("Open questions", "question", o.counts.pending_questions,
            o.counts.pending_questions === 1 ? "agent is waiting for an answer" : "agents waiting for an answer", "#/questions"),
          stat("Delivered", "send", d.delivered, `${d.pending + d.retry + d.processing} queued · ${d.dead_letter} failed`, "#/routes"),
          stat("Channels", "route", o.counts.enabled_providers,
            `${o.counts.providers} configured · ${o.counts.enabled_routes} of ${o.counts.routes} routes on`, "#/channels")),

        stuck ? notice(`${d.dead_letter} ${d.dead_letter === 1 ? "delivery has" : "deliveries have"} failed permanently. Check the channel settings and send a test.`, "warn") : null,

        h("div", { class: "grid-2" },
          card({
            title: "Broker",
            description: "The local process agents talk to.",
            body: h("dl", { class: "kv" },
              h("dt", { text: "Status" }), h("dd", null, badge("Running", "ok")),
              h("dt", { text: "Version" }), h("dd", { class: "mono", text: o.version }),
              h("dt", { text: "Platform" }), h("dd", { text: o.platform }),
              h("dt", { text: "Uptime" }), h("dd", { text: duration(o.uptime_seconds) }),
              h("dt", { text: "Local API" }), h("dd", { class: "mono", text: o.api_url }),
              h("dt", { text: "Process" }), h("dd", { class: "mono", text: String(o.pid) })),
          }),
          card({
            title: "This machine",
            description: "Where notifications appear and how credentials are kept.",
            body: h("dl", { class: "kv" },
              h("dt", { text: "Notifications" }), h("dd", { text: o.desktop_surface || "Not reported" }),
              h("dt", { text: "Credentials" }), h("dd", { text: o.secret_protection || "Not reported" }),
              h("dt", { text: "Data folder" }), h("dd", { class: "mono small", text: o.data_directory })),
          })),

        card({
          title: "Get an agent connected",
          description: "Agents need the skill to know AgentNotify exists, and a harness to route host approvals through it.",
          actions: button("Open agents", { iconName: "bot", onClick: () => ctx.navigate("agents") }),
          body: h("div", { class: "grid-3" },
            step("1", "Install the skill", "Teaches an agent when to notify you and how to ask a question.", "agents"),
            step("2", "Add a channel", "Optional. Reach your phone, chat, or mail when you are away from this screen.", "channels"),
            step("3", "Route what matters", "Decide which notifications leave this computer, by priority, type, project, or agent.", "routes")),
        }));
    };

    function step(number, title, text, to) {
      return h(to ? "a" : "div", { class: "step", href: to ? `#/${to}` : null },
        h("span", { class: "badge", text: `Step ${number}` }),
        h("strong", { text: title }),
        h("p", { class: "muted small", text }));
    }

    draw(ctx.overview() || await api.get("overview"));
    const timer = setInterval(async () => {
      if (document.visibilityState === "visible" && ctx.isCurrent()) {
        const next = await ctx.refreshOverview();
        if (next && ctx.isCurrent()) draw(next);
      }
    }, 15000);
    return () => clearInterval(timer);
  },
};
