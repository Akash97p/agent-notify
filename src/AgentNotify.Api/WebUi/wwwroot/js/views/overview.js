// The first page: what is waiting on you, how much capacity is left, where your notifications go,
// and what the router is doing. Each panel reads one projection and links to the page that owns it,
// so this stays a summary rather than a second copy of Attention, Usage, Quota, and Router.

import { api } from "../api.js";
import { h, mount, icon, card, pageHead, badge, notice, button } from "../dom.js";

const compact = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });
const precise = new Intl.NumberFormat();
const currency = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", maximumFractionDigits: 2 });
const pct = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
const providerNames = { codex: "Codex", claude_code: "Claude Code" };
const sourceNames = { claude_code: "Claude Code", codex: "Codex", opencode: "OpenCode", kilo: "Kilo", muse: "Muse Code", gemini_cli: "Gemini CLI" };

const tokens = (value) => compact.format(value || 0);
const money = (value) => currency.format(value || 0);
const clamp = (value) => Math.max(0, Math.min(100, Number(value) || 0));
const tone = (value) => value < 20 ? "danger" : value < 40 ? "warn" : "ok";
const when = (value) => value ? new Date(value).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" }) : "unknown";

// A panel that cannot reach its route says so in place; the rest of the page still renders.
const safely = (promise) => promise.then((value) => value, () => null);

function metric(label, iconName, value, detail, href) {
  return h("a", { class: "card dashboard-metric reveal", href },
    h("span", { class: "dashboard-metric-icon" }, icon(iconName)),
    h("div", null,
      h("span", { class: "stat-label", text: label }),
      h("strong", { class: "dashboard-metric-value", text: value }),
      h("span", { class: "stat-sub", text: detail })));
}

function balance(window) {
  const remaining = clamp(window.remaining_percent);
  return h("div", { class: "balance-row" },
    h("div", { class: "balance-head" },
      h("span", { class: "balance-label", text: window.label.replace(/^Codex · /, "") }),
      h("span", { class: `balance-value balance-${tone(remaining)}`, text: `${pct.format(remaining)}%` })),
    h("div", { class: "balance-track", role: "progressbar", "aria-label": `${window.label} remaining`,
      "aria-valuenow": remaining, "aria-valuemin": "0", "aria-valuemax": "100" },
      h("span", { class: `balance-fill balance-${tone(remaining)}`, style: { width: `${remaining}%` } })));
}

// ---- capacity ------------------------------------------------------------------------------

function lowestOf(providers) {
  let lowest = null;
  for (const provider of providers) {
    for (const window of provider.windows || []) {
      if (!lowest || window.remaining_percent < lowest.window.remaining_percent) lowest = { provider, window };
    }
  }
  return lowest;
}

function capacityPanel(quota, limit = 4) {
  if (!quota) return card({ title: "Capacity", body: notice("Live quota is not available from this broker.", "warn") });
  const accounts = (quota.providers || []).filter((provider) => provider.provider !== "opencode");
  const live = accounts.filter((provider) => provider.status === "ok" || provider.status === "stale");
  const shown = live.slice(0, limit);

  let body;
  if (!shown.length) {
    body = h("p", { class: "muted small", text: accounts.length
      ? "No Codex or Claude Code account reported a quota window. Sign in on this machine, then refresh Live quota."
      : "No Codex or Claude Code account is monitored yet." });
  } else {
    body = h("div", { class: "stack" }, shown.map((provider) =>
      h("div", { class: "dashboard-account" },
        h("div", { class: "row-between" },
          h("div", null,
            h("span", { class: "eyebrow", text: providerNames[provider.provider] || provider.provider }),
            h("strong", { class: "dashboard-account-name", text: provider.account_label })),
          badge(provider.status === "ok" ? "Live" : "Stale", provider.status === "ok" ? "ok" : "warn")),
        h("div", { class: "dashboard-account-windows" }, (provider.windows || []).map(balance)))));
  }

  return card({
    title: "Capacity",
    description: "Remaining allowance per account, lowest window first.",
    actions: h("a", { class: "small", href: "#/quota", text: live.length > limit ? `All ${live.length} accounts →` : "Live quota →" }),
    body,
  });
}

// ---- router --------------------------------------------------------------------------------

function routerPanel(router, ledger) {
  if (!router) return card({ title: "Model router", body: notice("The router is not available from this broker.", "warn") });

  const configured = router.upstreams || [];
  const routes = router.routes || [];
  const enabledUpstreams = configured.filter((upstream) => upstream.enabled).length;
  const totals = (ledger?.summary || []).reduce((sum, entry) => ({
    requests: sum.requests + entry.count, ok: sum.ok + entry.ok_count }), { requests: 0, ok: 0 });
  const busiest = (ledger?.summary || []).reduce((top, entry) => !top || entry.count > top.count ? entry : top, null);

  const body = [
    h("div", { class: "row-between" },
      badge(router.enabled ? "Routing" : "Off", router.enabled ? "ok" : "warn"),
      h("span", { class: "muted small", text: router.enabled ? "Agents may send model requests through this broker." : "Nothing is sent to a model provider until you turn it on." })),
    h("dl", { class: "kv" },
      h("dt", { text: "Upstreams" }), h("dd", { text: `${enabledUpstreams} of ${configured.length} enabled` }),
      h("dt", { text: "Routes" }), h("dd", { text: `${routes.filter((route) => route.enabled).length} of ${routes.length} on${router.switch_strategy ? ` · ${router.switch_strategy}` : ""}` }),
      h("dt", { text: "Default route" }), h("dd", { class: "small", text: router.default_route || "None" }),
      h("dt", { text: "Requests today" }),
      h("dd", { text: ledger?.summary ? `${precise.format(totals.requests)} · ${precise.format(totals.ok)} served` : "Not recorded" })),
    busiest ? h("p", { class: "muted small", text: `Busiest target: ${busiest.upstream_slug}/${busiest.model} (${precise.format(busiest.count)} requests).` }) : null,
  ].filter(Boolean);

  return card({
    title: "Model router",
    description: "Provider routing for the agents on this machine.",
    actions: h("a", { class: "small", href: "#/router", text: "Router →" }),
    body,
  });
}

// ---- notifications -------------------------------------------------------------------------

function notificationPanel(overview) {
  const delivery = overview.delivery;
  const queued = delivery.pending + delivery.processing + delivery.retry;
  const cell = (value, label, danger = false) =>
    h("div", null, h("strong", { class: danger && value > 0 ? "balance-danger" : "", text: precise.format(value) }), h("span", { text: label }));

  return card({
    title: "Notifications",
    description: "What is open on your side and what already left this computer.",
    actions: h("a", { class: "small", href: "#/attention", text: "Attention →" }),
    body: [
      h("div", { class: "health-grid" },
        cell(overview.counts.active_notifications, "Active"),
        cell(overview.counts.pending_questions, "Questions"),
        cell(delivery.delivered, "Delivered"),
        cell(queued, "Queued"),
        cell(delivery.dead_letter, "Failed", true)),
      h("p", { class: "muted small", text: `${overview.counts.enabled_providers} of ${overview.counts.providers} channels enabled · ${overview.counts.enabled_routes} of ${overview.counts.routes} routes on.` }),
      delivery.dead_letter > 0
        ? notice(`${delivery.dead_letter} ${delivery.dead_letter === 1 ? "delivery has" : "deliveries have"} failed permanently. Check the channel settings and send a test.`, "warn")
        : null,
    ].filter(Boolean),
  });
}

// ---- page ----------------------------------------------------------------------------------

function steps(ctx) {
  const step = (number, title, text, to) =>
    h("a", { class: "step", href: `#/${to}` },
      h("span", { class: "badge", text: `Step ${number}` }),
      h("strong", { text: title }),
      h("p", { class: "muted small", text }));

  return card({
    title: "Get an agent connected",
    description: "The skill teaches an agent when to notify you; a harness routes host approvals through it.",
    actions: button("Open agents", { iconName: "bot", onClick: () => ctx.navigate("agents") }),
    body: h("div", { class: "grid-3" },
      step("1", "Install the skill", "Teaches an agent when to notify you and how to ask a question.", "agents"),
      step("2", "Add a channel", "Optional. Reach your phone, chat, or mail when you are away from this screen.", "channels"),
      step("3", "Route what matters", "Decide which notifications leave this computer, by priority, type, project, or agent.", "routes")),
  });
}

export default {
  async render(page, ctx) {
    const draw = (data) => {
      const { overview, usage, quota, router, ledger } = data;
      if (!overview) {
        mount(page, pageHead("Overview", "The broker is not answering."),
          notice("The AgentNotify broker is not responding. Is it still running?", "danger"));
        return;
      }

      const live = (quota?.providers || []).filter((provider) =>
        provider.provider !== "opencode" && (provider.status === "ok" || provider.status === "stale"));
      const lowest = lowestOf(live);
      const tokenTotal = usage?.totals?.total;
      const topSource = usage?.sources?.length
        ? usage.sources.reduce((top, source) => !top || source.counts.total > top.counts.total ? source : top, null)
        : null;

      mount(page,
        pageHead("Overview", "Notifications, capacity, and routing on this computer, in one place.",
          button("Refresh", { iconName: "refresh", onClick: () => refresh() })),

        h("div", { class: "dashboard-metrics" },
          metric("Waiting on you", "bell", precise.format(overview.counts.active_notifications),
            `${overview.counts.pending_questions} waiting for an answer`, "#/attention"),
          metric("30-day tokens", "pulse", usage ? tokens(tokenTotal) : "—",
            usage ? `${precise.format(usage.session_count)} sessions · ${precise.format(usage.projects.length)} projects` : "Usage unavailable", "#/usage"),
          metric("Lowest balance", "gauge", lowest ? `${pct.format(lowest.window.remaining_percent)}%` : "—",
            lowest ? `${lowest.provider.account_label} · ${lowest.window.label.replace(/^Codex · /, "")}` : "No live quota window", "#/quota"),
          metric("API-equivalent cost", "chart", usage ? money(usage.cost.priced_usd) : "—",
            usage ? (usage.cost.complete ? "All records priced" : "Priced records only") : "Usage unavailable", "#/usage")),

        h("div", { class: "grid-2" },
          capacityPanel(quota),
          routerPanel(router, ledger)),

        h("div", { class: "grid-2" },
          notificationPanel(overview),
          steps(ctx)),

        h("p", { class: "muted small page-footnote", text: [
          `Usage scanned ${when(usage?.scanned_at)}`,
          `quota checked ${when(quota?.checked_at)}`,
          topSource ? `top agent ${sourceNames[topSource.source] || topSource.source}` : null,
          `broker up ${Math.round(overview.uptime_seconds / 60)} min`,
        ].filter(Boolean).join(" · ") + ". Account quotas, local usage, and router requests are separate sources and are never added together." }));
    };

    const load = async () => {
      const overview = (await safely(ctx.refreshOverview())) ?? (await safely(api.get("overview")));
      const [usage, quota, router, ledger] = await Promise.all([
        safely(api.get("usage?days=30")),
        safely(api.get("quota")),
        safely(api.get("router")),
        safely(api.get("router/summary?days=1")),
      ]);
      return { overview, usage, quota, router, ledger };
    };

    let data = await load();
    if (ctx.isCurrent()) draw(data);

    const refresh = async () => {
      mount(page, pageHead("Overview", "Refreshing…"));
      data = await load();
      if (ctx.isCurrent()) draw(data);
    };

    // The shell already polls /overview every 10s for the navigation counts; this re-reads the
    // panels less often so the page stays current without adding request pressure.
    const timer = setInterval(async () => {
      if (document.visibilityState !== "visible" || !ctx.isCurrent()) return;
      data = await load();
      if (ctx.isCurrent()) draw(data);
    }, 60000);
    return () => clearInterval(timer);
  },
};
