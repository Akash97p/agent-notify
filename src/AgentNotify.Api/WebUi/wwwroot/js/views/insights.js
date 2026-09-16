import { api } from "../api.js";
import { h, mount, pageHead, button, notice, icon, badge } from "../dom.js";

const number = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });
const precise = new Intl.NumberFormat();
const usd = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", maximumFractionDigits: 2 });
const pct = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
const sourceNames = { claude_code: "Claude Code", codex: "Codex", opencode: "OpenCode" };
const providerNames = { codex: "Codex", claude_code: "Claude Code" };
const sourceColors = { claude_code: "#8b5cf6", codex: "#3b82f6", opencode: "#14b8a6" };
const compact = (value) => number.format(value || 0);
const money = (value) => usd.format(value || 0);
const clamp = (value) => Math.max(0, Math.min(100, Number(value) || 0));
const balanceTone = (value) => value < 20 ? "danger" : value < 40 ? "warn" : "ok";

function metric(label, value, detail, iconName, href) {
  return h("a", { class: "card dashboard-metric reveal", href },
    h("span", { class: "dashboard-metric-icon" }, icon(iconName)),
    h("div", null,
      h("span", { class: "stat-label", text: label }),
      h("strong", { class: "dashboard-metric-value", text: value }),
      h("span", { class: "stat-sub", text: detail })));
}

function tinyBalance(window) {
  const remaining = clamp(window.remaining_percent);
  return h("div", { class: "dashboard-balance" },
    h("div", { class: "row-between" },
      h("span", { class: "small", text: window.label.replace(/^Codex · /, "") }),
      h("strong", { class: `balance-${balanceTone(remaining)}`, text: `${pct.format(remaining)}%` })),
    h("div", { class: "balance-track", role: "progressbar", "aria-label": `${window.label} remaining`,
      "aria-valuenow": remaining, "aria-valuemin": "0", "aria-valuemax": "100" },
      h("span", { class: `balance-fill balance-${balanceTone(remaining)}`, style: { width: `${remaining}%` } })));
}

function accountBalances(providers) {
  const accounts = providers.filter(provider => provider.provider !== "opencode");
  return h("div", { class: "dashboard-account-grid" }, accounts.map(provider =>
    h("div", { class: "dashboard-account" },
      h("div", { class: "row-between" },
        h("div", null,
          h("span", { class: "eyebrow", text: providerNames[provider.provider] || provider.provider }),
          h("strong", { class: "dashboard-account-name", text: provider.account_label })),
        badge(provider.status === "ok" ? "Live" : provider.status === "stale" ? "Stale" : "Unavailable",
          provider.status === "ok" ? "ok" : provider.status === "stale" ? "warn" : "danger")),
      provider.windows?.length
        ? h("div", { class: "dashboard-account-windows" }, provider.windows.map(tinyBalance))
        : h("span", { class: "muted small", text: provider.message || "No balance available" }))));
}

function dailyChart(days) {
  const recent = days.slice(-14);
  const max = Math.max(1, ...recent.map(day => day.counts.total));
  return h("div", { class: "dashboard-chart", role: "img", "aria-label": "Token usage by day for the last 14 active days" },
    recent.map((day, index) => {
      const height = Math.max(3, day.counts.total / max * 100);
      const label = new Date(`${day.date}T00:00:00`).toLocaleDateString(undefined, { month: "short", day: "numeric" });
      return h("div", { class: "chart-column", title: `${label}: ${precise.format(day.counts.total)} tokens · ${money(day.cost.priced_usd)}` },
        h("span", { class: "chart-value", text: compact(day.counts.total) }),
        h("span", { class: "chart-bar", style: { height: `${height}%`, "--delay": `${index * 28}ms` } }),
        h("span", { class: "chart-label", text: label }));
    }));
}

function sourceMix(sources) {
  const total = Math.max(1, sources.reduce((sum, source) => sum + source.counts.total, 0));
  let cursor = 0;
  const stops = sources.map(source => {
    const start = cursor;
    cursor += source.counts.total / total * 100;
    return `${sourceColors[source.source] || "#71717a"} ${start}% ${cursor}%`;
  });
  return h("div", { class: "source-mix" },
    h("div", { class: "donut", style: { background: `conic-gradient(${stops.join(",")})` },
      role: "img", "aria-label": "Thirty-day token share by coding agent" },
      h("div", { class: "donut-center" }, h("strong", { text: compact(total) }), h("span", { text: "tokens" }))),
    h("div", { class: "source-legend" }, sources.map(source => {
      const share = source.counts.total / total * 100;
      return h("div", { class: "source-legend-row" },
        h("span", { class: "legend-dot", style: { background: sourceColors[source.source] || "#71717a" } }),
        h("span", { class: "grow", text: sourceNames[source.source] || source.source }),
        h("strong", { text: `${pct.format(share)}%` }),
        h("span", { class: "muted small", text: money(source.cost.priced_usd) }));
    })));
}

function topProjects(projects) {
  const rows = projects.slice(0, 6);
  const max = Math.max(1, ...rows.map(project => project.counts.total));
  return h("div", { class: "project-ranking" }, rows.map((project, index) => {
    return h("div", { class: "project-rank" },
      h("div", { class: "row-between" },
        h("span", { class: "truncate", text: project.name }),
        h("span", { class: "muted small", text: `${compact(project.counts.total)} · ${money(project.cost.priced_usd)}` })),
      h("div", { class: "rank-track" },
        h("span", { class: "rank-fill", style: { width: `${project.counts.total / max * 100}%`, "--delay": `${index * 45}ms` } })));
  }));
}

function goBalances(go) {
  if (!go?.models?.length) return h("p", { class: "muted small", text: "No OpenCode Go estimate is available." });
  return h("div", { class: "go-summary" }, go.models.map(model => {
    const weekly = model.windows.find(window => window.key === "seven_day") || model.windows[0];
    const known = weekly?.estimated_used_percent != null;
    const remaining = known ? Math.max(0, 100 - weekly.estimated_used_percent) : null;
    return h("div", { class: "go-summary-row" },
      h("div", { class: "grow" },
        h("strong", { class: "mono small", text: model.model }),
        h("span", { class: "muted small", text: weekly?.label || "Local estimate" })),
      h("strong", { class: remaining == null ? "" : `balance-${balanceTone(remaining)}`,
        text: remaining == null ? "Unknown" : `${pct.format(remaining)}% left` }));
  }));
}

function deliveryHealth(overview) {
  const delivery = overview.delivery;
  const queued = delivery.pending + delivery.processing + delivery.retry;
  return h("div", { class: "health-grid" },
    h("div", null, h("strong", { text: precise.format(overview.counts.active_notifications) }), h("span", { text: "Active" })),
    h("div", null, h("strong", { text: precise.format(overview.counts.pending_questions) }), h("span", { text: "Questions" })),
    h("div", null, h("strong", { text: precise.format(delivery.delivered) }), h("span", { text: "Delivered" })),
    h("div", null, h("strong", { text: precise.format(queued) }), h("span", { text: "Queued" })),
    h("div", null, h("strong", { class: delivery.dead_letter ? "balance-danger" : "", text: precise.format(delivery.dead_letter) }), h("span", { text: "Failed" })));
}

export default {
  async render(page, ctx) {
    const refresh = button("Refresh", { iconName: "refresh" });
    const load = async () => {
      refresh.disabled = true;
      try {
        const [overview, usage, quota] = await Promise.all([
          api.get("overview"), api.get("usage?days=30"), api.get("quota")]);
        if (!ctx.isCurrent()) return;
        if (usage.contract_version !== "3" || quota.contract_version !== "2")
          throw new Error("The dashboard and broker need to be updated together.");

        const accounts = quota.providers.filter(provider => provider.provider !== "opencode");
        const live = accounts.filter(provider => provider.status === "ok" || provider.status === "stale");
        const windows = live.flatMap(provider => provider.windows.map(window => ({ provider, window })));
        const lowest = windows.reduce((result, item) => !result || item.window.remaining_percent < result.window.remaining_percent ? item : result, null);
        mount(page,
          pageHead("Dashboard", "Your coding-agent activity and remaining capacity in one view.", refresh),
          h("div", { class: "dashboard-metrics" },
            metric("Live accounts", `${live.length} / ${accounts.length}`, "Codex and Claude profiles", "gauge", "#/quota"),
            metric("Lowest balance", lowest ? `${pct.format(lowest.window.remaining_percent)}%` : "—",
              lowest ? `${lowest.provider.account_label} · ${lowest.window.label.replace(/^Codex · /, "")}` : "No live windows", "alert", "#/quota"),
            metric("30-day tokens", compact(usage.totals.total), `${precise.format(usage.session_count)} sessions · ${usage.projects.length} projects`, "pulse", "#/usage"),
            metric("API-equivalent cost", money(usage.cost.priced_usd), usage.cost.complete ? "All records priced" : "Priced records", "chart", "#/usage")),
          h("section", { class: "card dashboard-panel reveal" },
            h("div", { class: "dashboard-panel-head" },
              h("div", null, h("h2", { text: "Account balances" }), h("p", { class: "muted small", text: "The bars shrink as each allowance is used." })),
              h("a", { class: "small", href: "#/quota", text: "View details →" })),
            accountBalances(quota.providers)),
          h("div", { class: "dashboard-grid" },
            h("section", { class: "card dashboard-panel dashboard-wide reveal" },
              h("div", { class: "dashboard-panel-head" },
                h("div", null, h("h2", { text: "Usage trend" }), h("p", { class: "muted small", text: "Last 14 active days · hover a bar for exact values" })),
                h("a", { class: "small", href: "#/usage", text: "Usage details →" })),
              dailyChart(usage.daily)),
            h("section", { class: "card dashboard-panel reveal" },
              h("div", { class: "dashboard-panel-head" }, h("h2", { text: "Agent mix" })),
              sourceMix(usage.sources)),
            h("section", { class: "card dashboard-panel reveal" },
              h("div", { class: "dashboard-panel-head" }, h("h2", { text: "OpenCode Go" }), badge("Local estimate", "info")),
              goBalances(quota.open_code_go)),
            h("section", { class: "card dashboard-panel reveal" },
              h("div", { class: "dashboard-panel-head" }, h("h2", { text: "Top projects" }), h("a", { class: "small", href: "#/usage", text: "All projects →" })),
              topProjects(usage.projects)),
            h("section", { class: "card dashboard-panel reveal" },
              h("div", { class: "dashboard-panel-head" }, h("h2", { text: "Broker health" }), badge(overview.platform, "ok")),
              deliveryHealth(overview))),
          h("p", { class: "muted small page-footnote", text: `Usage scanned ${new Date(usage.scanned_at).toLocaleString()} · quota checked ${new Date(quota.checked_at).toLocaleString()}. Account quotas and local usage are separate data sources.` }));
      } catch (error) {
        if (ctx.isCurrent()) mount(page, pageHead("Dashboard", "Coding-agent activity and capacity.", refresh), notice(error.message, "danger"));
      } finally { refresh.disabled = false; }
    };
    refresh.addEventListener("click", load);
    mount(page, pageHead("Dashboard", "Loading activity and account balances…", refresh),
      h("div", { class: "dashboard-metrics" }, [0, 1, 2, 3].map(() => h("div", { class: "skeleton dashboard-skeleton" }))));
    await load();
  },
};
