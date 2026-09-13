import { api } from "../api.js";
import { h, mount, card, pageHead, button, select, notice, empty } from "../dom.js";

const fmt = new Intl.NumberFormat();
const usd = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", minimumFractionDigits: 2, maximumFractionDigits: 2 });
const smallUsd = new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", minimumFractionDigits: 4, maximumFractionDigits: 4 });
const sourceName = (source) => source === "claude_code" ? "Claude Code" : source === "codex" ? "Codex" : source;
const n = (value) => fmt.format(value || 0);
const money = (value) => value > 0 && value < 0.0001 ? "<$0.0001" : value > 0 && value < 0.01 ? smallUsd.format(value) : usd.format(value || 0);
const costText = (cost) => cost.unpriced_events ? `${money(cost.priced_usd)} + unpriced` : money(cost.priced_usd);

function metric(label, value, detail) {
  return h("div", { class: "card stat" },
    h("span", { class: "stat-label", text: label }),
    h("span", { class: "stat-value", text: n(value) }),
    h("span", { class: "stat-sub", text: detail }));
}

function modelsTable(models) {
  return h("div", { class: "table-wrap" },
    h("table", { class: "table" },
      h("thead", null, h("tr", null,
        h("th", { text: "Agent" }), h("th", { text: "Model" }), h("th", { text: "Input" }),
        h("th", { text: "Cache read" }), h("th", { text: "Cache write" }), h("th", { text: "Output" }),
        h("th", { text: "API cost" }))),
      h("tbody", null, models.map((row) => h("tr", null,
        h("td", { text: sourceName(row.source) }),
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
  return h("div", { class: "usage-projects" }, projects.map((project) => {
    const label = names.get(project.name) > 1 ? `${project.name} · ${project.id.slice(-4)}` : project.name;
    return h("details", { class: "usage-project" },
      h("summary", null,
        h("span", { class: "usage-project-name", text: label }),
        h("span", { class: "usage-project-tokens", text: `${n(project.counts.total)} tokens` }),
        h("strong", { text: costText(project.cost) })),
      modelsTable(project.models));
  }));
}

function dailyChart(days) {
  const recent = days.slice(-30);
  const max = Math.max(1, ...recent.map((day) => day.counts.total));
  return h("div", { class: "usage-days" }, recent.map((day) =>
    h("div", { class: "usage-day" },
      h("span", { class: "usage-date", text: day.date }),
      h("span", { class: "usage-track" },
        h("span", { class: "usage-fill", style: { width: `${Math.max(1, day.counts.total / max * 100)}%` } })),
      h("strong", { text: n(day.counts.total) }))));
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
      const counts = data.totals;
      mount(page,
        pageHead("Usage", "Local token history and what it would cost at published standard API rates. This is not a bill or subscription usage.",
          h("div", { class: "row" }, periodControl, refresh)),
        data.files_skipped ? notice(`${data.files_skipped} log file(s) could not be read, so totals may be incomplete.`, "warn") : null,
        data.cost.unpriced_events ? notice(`${n(data.cost.unpriced_events)} usage records (${n(data.cost.unpriced_tokens)} tokens) have no verified model price. Cost totals include only priced records.`, "warn") : null,
        data.events === 0 ? card({ body: empty("No usage records found", "Use Claude Code or Codex on this computer, then refresh. AgentNotify reads their local session logs; no setup is needed.", "pulse") }) : [
          h("div", { class: "stats usage-stats" },
            metric("Total tokens", counts.total, `${n(data.events)} usage records`),
            h("div", { class: "card stat" },
              h("span", { class: "stat-label", text: "API-equivalent cost" }),
              h("span", { class: "stat-value", text: money(data.cost.priced_usd) }),
              h("span", { class: "stat-sub", text: data.cost.complete ? "Estimated at standard public rates" : "Priced records only; see warning" })),
            metric("Cache read", counts.cache_read, "Reused prompt tokens"),
            metric("Output", counts.output, `${n(counts.reasoning)} reasoning tokens included`)),
          h("div", { class: "grid-2" },
            card({ title: "By agent", description: "Locally recorded tokens for this period.",
              body: h("div", { class: "usage-sources" }, data.sources.map((source) =>
                h("div", { class: "row-between" },
                  h("span", { text: sourceName(source.source) }),
                  h("strong", { text: costText(source.cost) }),
                  h("span", { class: "muted small", text: `${n(source.counts.total)} tokens` })))) }),
            card({ title: "Pricing basis", description: `Published standard text-token API rates as of ${data.pricing_as_of}.`,
              body: h("div", { class: "stack" },
                h("p", { class: "muted small", text: "Current rates are applied to the selected history. Claude cache reads and 5-minute/1-hour writes have separate prices; Codex cached input is counted once. This excludes plan allowances, Fast/Batch, long-context premiums, tools, taxes, and discounts." }),
                h("div", { class: "row" },
                  h("a", { href: "https://developers.openai.com/api/docs/models", target: "_blank", rel: "noopener noreferrer", text: "OpenAI prices ↗" }),
                  h("a", { href: "https://platform.claude.com/docs/en/about-claude/pricing", target: "_blank", rel: "noopener noreferrer", text: "Claude prices ↗" }))) })),
          card({ title: "By project", description: "Working-directory projects. Open one to see its models; full paths stay on the broker.", body: projectsList(data.projects) }),
          card({ title: "By model", description: "Up to 30 models, ordered by token volume.", body: modelsTable(data.models) }),
          card({ title: "Recent active days", description: "Local calendar days with recorded usage; up to 30 shown.", body: dailyChart(data.daily) })
        ],
        h("p", { class: "muted small", text: `${n(data.files_scanned)} local log files checked · refreshed ${new Date(data.scanned_at).toLocaleString()}. Prompt and response text is never returned by this page.` }));
    }

    await load();
  },
};
