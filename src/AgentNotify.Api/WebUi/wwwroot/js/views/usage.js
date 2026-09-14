import { api } from "../api.js";
import { h, mount, card, pageHead, button, select, notice, empty } from "../dom.js";

const fmt = new Intl.NumberFormat();
const compact = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });
const usd = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: 2 });
const smallUsd = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", minimumFractionDigits: 4, maximumFractionDigits: 4 });
const sourceName = (source) => source === "claude_code" ? "Claude Code" : source === "codex" ? "Codex" : source === "opencode" ? "OpenCode" : source;
const providerName = (provider) => provider === "openai" ? "OpenAI" : provider === "anthropic" ? "Anthropic" : provider === "opencode-go" ? "OpenCode Go" : provider;
const n = (value) => fmt.format(value || 0);
const short = (value) => compact.format(value || 0);
const money = (value) => value > 0 && value < 0.0001 ? "<$0.0001" : value > 0 && value < 0.01 ? smallUsd.format(value) : usd.format(value || 0);
const costText = (cost) => cost.unpriced_events ? `${money(cost.priced_usd)} + unpriced` : money(cost.priced_usd);

function metric(label, value, detail) {
  return h("div", { class: "card stat reveal" },
    h("span", { class: "stat-label", text: label }),
    h("span", { class: "stat-value", text: value }),
    h("span", { class: "stat-sub", text: detail }));
}

function modelsTable(models) {
  return h("div", { class: "table-wrap" },
    h("table", { class: "table" },
      h("thead", null, h("tr", null,
        h("th", { text: "Agent" }), h("th", { text: "Provider" }), h("th", { text: "Model" }), h("th", { text: "Input" }),
        h("th", { text: "Cache read" }), h("th", { text: "Cache write" }), h("th", { text: "Output" }),
        h("th", { text: "Est. cost" }))),
      h("tbody", null, models.map(row => h("tr", null,
        h("td", { text: sourceName(row.source) }),
        h("td", { text: providerName(row.provider) }),
        h("td", { class: "mono small", text: row.model }),
        h("td", { text: n(row.counts.input) }),
        h("td", { text: n(row.counts.cache_read) }),
        h("td", { text: n(row.counts.cache_write) }),
        h("td", { text: n(row.counts.output) }),
        h("td", { text: costText(row.cost) }))))));
}

function projectsList(projects) {
  const names = new Map();
  for (const project of projects) names.set(project.name, (names.get(project.name) || 0) + 1);
  return h("div", { class: "usage-projects" }, projects.map(project => {
    const label = names.get(project.name) > 1 ? `${project.name} · ${project.id.slice(-4)}` : project.name;
    return h("details", { class: "usage-project" },
      h("summary", null,
        h("span", { class: "usage-project-name", text: label }),
        h("span", { class: "usage-project-tokens", text: `${n(project.counts.total)} tokens` }),
        h("strong", { text: costText(project.cost) })),
      modelsTable(project.models));
  }));
}

function sessionsList(sessions) {
  return h("div", { class: "usage-projects" }, sessions.map(session =>
    h("details", { class: "usage-project" },
      h("summary", null,
        h("span", { class: "usage-project-name", text: `${sourceName(session.source)} · ${session.project_name} · ${session.id.slice(-4)}` }),
        h("span", { class: "usage-project-tokens", text: `${new Date(session.ended_at).toLocaleString()} · ${n(session.counts.total)} tokens` }),
        h("strong", { text: costText(session.cost) })),
      h("div", { class: "muted small session-meta",
        text: `${new Date(session.started_at).toLocaleString()} to ${new Date(session.ended_at).toLocaleString()} · ${n(session.events)} usage records` }),
      modelsTable(session.models))));
}

function dailyChart(days) {
  const recent = days.slice(-30);
  const max = Math.max(1, ...recent.map(day => day.counts.total));
  return h("div", { class: "usage-days" }, recent.map((day, index) =>
    h("div", { class: "usage-day", title: `${money(day.cost.priced_usd)} estimated API-equivalent cost` },
      h("span", { class: "usage-date", text: day.date }),
      h("span", { class: "usage-track" },
        h("span", { class: "usage-fill", style: { width: `${Math.max(1, day.counts.total / max * 100)}%`, "--delay": `${index * 20}ms` } })),
      h("strong", { text: short(day.counts.total) }))));
}

function agentSummary(sources, total) {
  return h("div", { class: "agent-summary" }, sources.map(source => {
    const share = total ? source.counts.total / total * 100 : 0;
    return h("div", { class: "agent-summary-row" },
      h("div", { class: "row-between" },
        h("strong", { text: sourceName(source.source) }),
        h("strong", { text: costText(source.cost) })),
      h("div", { class: "agent-share-track" }, h("span", { style: { width: `${share}%` } })),
      h("span", { class: "muted small", text: `${short(source.counts.total)} tokens · ${share.toFixed(1)}%` }));
  }));
}

function disclosure(title, description, body, open = false) {
  return h("details", { class: "card report-disclosure reveal", open },
    h("summary", null, h("div", null,
      h("h2", { class: "card-title", text: title }),
      h("p", { class: "card-desc", text: description }))),
    h("div", { class: "report-disclosure-body" }, body));
}

export default {
  async render(page, ctx) {
    let period = "30";
    const periodControl = select([["7", "Last 7 days"], ["30", "Last 30 days"], ["all", "All history"]], period,
      { "aria-label": "Usage period" });
    const refresh = button("Refresh", { iconName: "refresh" });
    const load = async () => {
      refresh.disabled = true;
      try {
        const data = await api.get(`usage?days=${encodeURIComponent(period)}`);
        if (!ctx.isCurrent()) return;
        draw(data);
      } catch (error) {
        if (ctx.isCurrent()) mount(page, pageHead("Usage", "Local token history from coding-agent logs."), notice(error.message, "danger"));
      } finally { refresh.disabled = false; }
    };
    periodControl.addEventListener("change", () => { period = periodControl.value; load(); });
    refresh.addEventListener("click", load);

    function draw(data) {
      if (data.contract_version !== "2") throw new Error("The Usage page and broker need to be updated together.");
      const counts = data.totals;
      mount(page,
        pageHead("Usage", "Local activity with API-equivalent token cost.",
          h("div", { class: "row" }, periodControl, refresh)),
        data.files_skipped ? notice(`${data.files_skipped} local usage store(s) could not be read, so totals may be incomplete.`, "warn") : null,
        data.events === 0 ? card({ body: empty("No usage records found", "Use Claude Code, Codex, or OpenCode on this computer, then refresh.", "pulse") }) : [
          h("div", { class: "stats usage-stats compact-stats" },
            metric("Tokens", short(counts.total), `${n(data.events)} usage records`),
            metric("API-equivalent cost", money(data.cost.priced_usd), data.cost.complete ? "All local records priced" : "Priced local records"),
            metric("Sessions", n(data.session_count), `${n(data.projects.length)} projects`)),
          h("div", { class: "grid-2 usage-overview-grid" },
            card({ title: "By agent", description: "Share of local token activity.", className: "reveal",
              body: agentSummary(data.sources, counts.total) }),
            card({ title: "Active days", description: "Relative token volume; hover for cost.", className: "reveal",
              body: dailyChart(data.daily) })),
          disclosure("Recent sessions", `${n(data.session_count)} sessions; latest ${n(data.sessions.length)} available.`,
            data.sessions.length ? sessionsList(data.sessions) : empty("No session IDs found", "This history has no attributable session IDs.", "pulse")),
          disclosure("Projects", `${n(data.projects.length)} working-directory projects. Full paths stay on the broker.`, projectsList(data.projects)),
          disclosure("Models", `${n(data.models.length)} models ordered by token volume.`, modelsTable(data.models)),
          disclosure("Token and pricing details", `Published text-token rates as of ${data.pricing_as_of}.`,
            h("div", { class: "stack" },
              h("dl", { class: "kv" },
                h("dt", { text: "Input" }), h("dd", { text: n(counts.input) }),
                h("dt", { text: "Cache read" }), h("dd", { text: n(counts.cache_read) }),
                h("dt", { text: "Cache write" }), h("dd", { text: n(counts.cache_write) }),
                h("dt", { text: "Output" }), h("dd", { text: n(counts.output) }),
                h("dt", { text: "Reasoning" }), h("dd", { text: `${n(counts.reasoning)} · included in output where the provider reports it that way` })),
              h("p", { class: "muted small", text: "OpenAI and Claude use standard API rates. OpenCode Go uses published quota-equivalent token rates. Estimates exclude plan allowances, Fast/Batch, long-context premiums, tools, taxes, and discounts." }),
              h("div", { class: "row" },
                h("a", { href: "https://developers.openai.com/api/docs/models", target: "_blank", rel: "noopener noreferrer", text: "OpenAI prices ↗" }),
                h("a", { href: "https://platform.claude.com/docs/en/about-claude/pricing", target: "_blank", rel: "noopener noreferrer", text: "Claude prices ↗" }),
                h("a", { href: "https://opencode.ai/docs/go/", target: "_blank", rel: "noopener noreferrer", text: "OpenCode Go prices ↗" }))))
        ],
        h("p", { class: "muted small page-footnote", text: `${n(data.files_scanned)} local usage stores checked · refreshed ${new Date(data.scanned_at).toLocaleString()}. Prompt and response text never leaves the broker.` }));
    }

    mount(page, pageHead("Usage", "Loading local activity…", h("div", { class: "row" }, periodControl, refresh)), h("div", { class: "skeleton" }));
    await load();
  },
};
