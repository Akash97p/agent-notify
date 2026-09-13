import { api } from "../api.js";
import { h, mount, card, pageHead, button, notice } from "../dom.js";

const names = { codex: "Codex", claude_code: "Claude Code", opencode: "OpenCode" };
const percent = (value) => `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value)}%`;
const when = (value) => value ? new Date(value).toLocaleString() : "Reset time not reported";

function windowRow(window) {
  return h("div", { class: "quota-window" },
    h("div", { class: "row-between" },
      h("strong", { text: window.label }),
      h("span", { text: `${percent(window.used_percent)} used · ${percent(window.remaining_percent)} left` })),
    h("div", { class: "quota-track", role: "progressbar", "aria-label": window.label,
      "aria-valuenow": window.used_percent, "aria-valuemin": "0", "aria-valuemax": "100" },
      h("span", { class: "quota-fill", style: { width: `${window.used_percent}%` } })),
    h("span", { class: "muted small", text: `Resets: ${when(window.resets_at)}` }));
}

function providerCard(provider) {
  const title = names[provider.provider] || provider.provider;
  const details = provider.status === "ok" || provider.status === "stale"
    ? h("div", { class: "stack" },
        provider.status === "stale" ? notice("Last known quota is shown because the latest check failed.", "warn") : null,
        provider.windows.length ? h("div", { class: "quota-windows" }, provider.windows.map(windowRow)) : null,
        provider.credit_balance != null ? h("p", { class: "small", text: `Credit balance: $${Number(provider.credit_balance).toFixed(2)}` }) : null,
        h("p", { class: "muted small", text: `${provider.source}${provider.plan ? ` · ${provider.plan} plan` : ""}${provider.fetched_at ? ` · checked ${new Date(provider.fetched_at).toLocaleString()}` : ""}` }))
    : h("p", { class: "muted", text: provider.message || "No live account quota is available." });
  return card({ title, description: provider.status === "ok" ? "Current account allowance" :
    provider.status === "stale" ? "Last known account allowance" : "Quota unavailable", body: details });
}

export default {
  async render(page, ctx) {
    const refresh = button("Check now", { iconName: "refresh" });
    const draw = (report) => mount(page,
      pageHead("Live quota", "Provider-reported account allowance and reset times. Separate from local Usage and estimated cost.", refresh),
      h("div", { class: "quota-grid" }, report.providers.map(providerCard)),
      h("p", { class: "muted small", text: "Checks are cached for five minutes; manual refresh is limited to once every 30 seconds. A missing limit is never shown as 0%. OpenCode uses multiple providers and has no single account quota." }));
    const load = async (force = false) => {
      refresh.disabled = true;
      try {
        const report = force ? await api.post("quota/refresh") : await api.get("quota");
        if (ctx.isCurrent()) draw(report);
      } catch (error) {
        if (ctx.isCurrent()) mount(page, pageHead("Live quota", "Provider-reported account allowance.", refresh), notice(error.message, "danger"));
      } finally { refresh.disabled = false; }
    };
    refresh.addEventListener("click", () => load(true));
    mount(page, pageHead("Live quota", "Checking provider account allowances…", refresh));
    await load();
  },
};
