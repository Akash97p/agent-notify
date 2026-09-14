import { api } from "../api.js";
import { h, mount, card, pageHead, button, notice, field, input, select, busy, toast, confirmDialog } from "../dom.js";

const names = { codex: "Codex", claude_code: "Claude Code" };
const percent = (value) => `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value)}%`;
const money = (value) => new Intl.NumberFormat(undefined, { style: "currency", currency: "USD", maximumFractionDigits: 6 }).format(value);
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
  const title = `${names[provider.provider] || provider.provider} · ${provider.account_label || "Current account"}`;
  const details = provider.status === "ok" || provider.status === "stale"
    ? h("div", { class: "stack" },
        provider.status === "stale" ? notice("Last known quota is shown because the latest check failed.", "warn") : null,
        provider.windows.length ? h("div", { class: "quota-windows" }, provider.windows.map(windowRow)) : null,
        provider.credit_balance != null ? h("p", { class: "small", text: `Credit balance: ${money(provider.credit_balance)}` }) : null,
        h("p", { class: "muted small", text: `${provider.source}${provider.plan ? ` · ${provider.plan} plan` : ""}${provider.fetched_at ? ` · checked ${new Date(provider.fetched_at).toLocaleString()}` : ""}` }))
    : h("p", { class: "muted", text: provider.message || "No live account quota is available." });
  return card({ title, description: provider.status === "ok" ? "Current account allowance" :
    provider.status === "stale" ? "Last known account allowance" : "Quota unavailable", body: details });
}

function goWindowRow(window) {
  const complete = window.estimated_used_percent != null;
  const summary = complete
    ? `${money(window.observed_usd)} locally observed / ${money(window.limit_usd)} published cap · ${percent(window.estimated_used_percent)}`
    : `${window.unpriced_records} local records have no verified Go rate; percentage unknown`;
  return h("div", { class: "quota-window" },
    h("div", { class: "row-between" }, h("strong", { text: window.label }), h("span", { text: summary })),
    complete ? h("div", { class: "quota-track", role: "progressbar", "aria-label": window.label,
      "aria-valuenow": Math.min(window.estimated_used_percent, 100), "aria-valuemin": "0", "aria-valuemax": "100" },
      h("span", { class: "quota-fill", style: { width: `${Math.min(window.estimated_used_percent, 100)}%` } })) : null);
}

function goSection(go) {
  return h("section", { class: "stack" },
    h("h2", { text: "OpenCode Go · local estimate" }),
    notice(go.message, "info"),
    h("p", { class: "muted small" }, "The local windows roll backward from now; the provider's billing cycle and reset times are unknown. Rates checked ",
      go.pricing_as_of, ". ", h("a", { href: "https://opencode.ai/docs/go/", target: "_blank", rel: "noreferrer", text: "OpenCode Go limits" })),
    go.models?.length ? h("div", { class: "quota-grid" }, go.models.map(model =>
      card({ title: model.model, description: "Per-model published dollar cap, estimated from this machine only",
        body: h("div", { class: "quota-windows" }, model.windows.map(goWindowRow)) }))) : null);
}

function accountEditor(accounts, reload) {
  const provider = select([["codex", "Codex / OpenAI"], ["claude_code", "Claude Code / Anthropic"]], "codex");
  const label = input({ placeholder: "Personal, work, second account…", required: true, maxlength: 60 });
  const directory = input({ placeholder: "~/.codex-second", required: true, maxlength: 1024 });
  const help = h("p", { class: "muted small" });
  const updateHelp = () => {
    directory.placeholder = provider.value === "codex" ? "~/.codex-second" : "~/.claude-second";
    help.textContent = provider.value === "codex"
      ? "Sign in to Codex with CODEX_HOME set to this directory. AgentNotify asks that Codex profile for its quota."
      : "Sign in to Claude Code with CLAUDE_CONFIG_DIR set to this directory. AgentNotify reads that profile's local OAuth credential, if present.";
  };
  provider.addEventListener("change", updateHelp);
  updateHelp();
  const add = button("Add account", { variant: "primary", type: "submit" });
  const form = h("form", { class: "stack" },
    h("div", { class: "grid-2" },
      field("Agent", provider, { required: true }), field("Account name", label, { required: true })),
    field("Agent profile directory", directory, { required: true }), help, add);
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    await busy(add, async () => {
      try {
        await api.post("quota/accounts", { provider: provider.value, label: label.value, directory: directory.value });
        toast("Account added. Sign in to that profile if its quota is unavailable.");
        await reload(true);
      } catch (error) { toast(error.message, "error"); }
    });
  });
  const extras = accounts.filter(account => !account.is_default);
  return card({ title: "Monitor another account", description: "Add each signed-in agent profile once. Account names and directories stay on this broker; credentials remain with Codex or Claude Code.",
    body: h("div", { class: "stack" }, form,
      extras.length ? h("div", { class: "list", role: "list" }, extras.map(account => {
        const remove = button("Remove", { variant: "ghost", size: "sm" });
        remove.addEventListener("click", () => busy(remove, async () => {
          const confirmed = await confirmDialog({ title: `Stop monitoring ${account.label}?`,
            message: "The agent's profile and sign-in are left untouched.", confirmLabel: "Remove", danger: true });
          if (!confirmed) return;
          try { await api.del(`quota/accounts/${encodeURIComponent(account.id)}`); await reload(); }
          catch (error) { toast(error.message, "error"); }
        }));
        return h("div", { class: "list-item", role: "listitem" },
          h("div", { class: "list-main" },
            h("strong", { text: `${names[account.provider]} · ${account.label}` }),
            h("span", { class: "small muted mono", text: account.directory })), remove);
      })) : null) });
}

export default {
  async render(page, ctx) {
    const refresh = button("Check now", { iconName: "refresh" });
    const load = async (force = false) => {
      refresh.disabled = true;
      try {
        const [report, configured] = await Promise.all([
          force ? api.post("quota/refresh") : api.get("quota"), api.get("quota/accounts")]);
        if (!ctx.isCurrent()) return;
        if (report.contract_version !== "2") throw new Error("The broker and page use different quota contracts. Reload the page.");
        mount(page,
          pageHead("Live quota", "Provider-reported account allowances plus a separate local OpenCode Go estimate.", refresh),
          h("div", { class: "quota-grid" }, report.providers.filter(provider => provider.provider !== "opencode").map(providerCard)),
          report.open_code_go ? goSection(report.open_code_go) : null,
          accountEditor(configured.accounts, load),
          h("p", { class: "muted small", text: "Account checks are cached for five minutes; manual refresh is limited to once every 30 seconds. Missing limits are never shown as 0%." }));
      } catch (error) {
        if (ctx.isCurrent()) mount(page, pageHead("Live quota", "Provider-reported account allowance.", refresh), notice(error.message, "danger"));
      } finally { refresh.disabled = false; }
    };
    refresh.addEventListener("click", () => load(true));
    mount(page, pageHead("Live quota", "Checking provider account allowances…", refresh));
    await load();
  },
};
