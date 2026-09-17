import { api } from "../api.js";
import { h, mount, pageHead, button, notice, field, input, select, busy, toast, confirmDialog, badge } from "../dom.js";

const names = { codex: "Codex", claude_code: "Claude Code" };
const percent = (value) => `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value)}%`;
const money = (value) => new Intl.NumberFormat(undefined,
  { style: "currency", currency: "USD", maximumFractionDigits: 4 }).format(value);
const when = (value) => value ? new Date(value).toLocaleString() : "Reset time unavailable";
const clamp = (value) => Math.max(0, Math.min(100, Number(value) || 0));
const tone = (remaining) => remaining < 20 ? "danger" : remaining < 40 ? "warn" : "ok";

function balanceBar(label, remaining, detail, reset) {
  const value = clamp(remaining);
  return h("div", { class: "balance-row" },
    h("div", { class: "balance-head" },
      h("span", { class: "balance-label", text: label.replace(/^Codex · /, "") }),
      h("strong", { class: `balance-value balance-${tone(value)}`, text: `${percent(value)} left` })),
    h("div", { class: "balance-track", role: "progressbar", "aria-label": `${label} remaining balance`,
      "aria-valuenow": value, "aria-valuemin": "0", "aria-valuemax": "100", "aria-valuetext": `${percent(value)} remaining` },
      h("span", { class: `balance-fill balance-${tone(value)}`, style: { width: `${value}%` } })),
    h("div", { class: "balance-meta" },
      h("span", { text: detail }), reset ? h("span", { text: `Resets ${when(reset)}` }) : null));
}

function providerCard(provider) {
  const ready = provider.status === "ok" || provider.status === "stale";
  const title = names[provider.provider] || provider.provider;
  return h("section", { class: "card quota-account reveal" },
    h("div", { class: "quota-account-head" },
      h("div", null,
        h("span", { class: "eyebrow", text: title }),
        h("h2", { class: "quota-account-name", text: provider.account_label || "Current account" })),
      badge(provider.status === "ok" ? "Live" : provider.status === "stale" ? "Stale" : "Unavailable",
        provider.status === "ok" ? "ok" : provider.status === "stale" ? "warn" : "danger")),
    ready ? h("div", { class: "quota-account-body" },
      provider.status === "stale" ? h("p", { class: "compact-warning", text: "Showing the last successful check." }) : null,
      provider.windows.map(window => balanceBar(window.label, window.remaining_percent,
        `${percent(window.used_percent)} used`, window.resets_at)),
      provider.credit_balance != null ? h("div", { class: "quota-credit" },
        h("span", { text: "Credits" }), h("strong", { text: money(provider.credit_balance) })) : null,
      h("details", { class: "meta-disclosure" },
        h("summary", { text: "Account details" }),
        h("div", { class: "meta-lines" },
          provider.plan ? h("span", { text: `Plan: ${provider.plan}` }) : null,
          h("span", { text: `Source: ${provider.source}` }),
          provider.fetched_at ? h("span", { text: `Checked: ${new Date(provider.fetched_at).toLocaleString()}` }) : null)))
      : h("div", { class: "quota-account-body" },
          h("p", { class: "muted small", text: provider.message || "No live allowance is available." }))) ;
}

function goCard(model) {
  return h("section", { class: "card quota-account reveal" },
    h("div", { class: "quota-account-head" },
      h("div", null, h("span", { class: "eyebrow", text: "OpenCode Go" }),
        h("h2", { class: "quota-account-name mono", text: model.model })),
      badge("Estimate", "info")),
    h("div", { class: "quota-account-body" }, model.windows.map(window => {
      const complete = window.estimated_used_percent != null;
      if (!complete) return h("div", { class: "balance-row" },
        h("div", { class: "balance-head" }, h("span", { class: "balance-label", text: window.label }),
          h("strong", { text: "Unknown" })),
        h("span", { class: "balance-meta", text: `${window.unpriced_records} unpriced local records` }));
      const remaining = Math.max(0, 100 - window.estimated_used_percent);
      return balanceBar(window.label, remaining,
        `${money(window.observed_usd)} observed of ${money(window.limit_usd)} cap`, window.resets_at);
    })));
}

function goRenewal(go, reload) {
  const day = input({ type: "number", min: "1", max: "31", step: "1", value: go.renewal_day ?? "",
    placeholder: "Not set", "aria-label": "OpenCode Go renewal day of the month", class: "input go-renewal-day" });
  const save = button("Save", { size: "sm" });
  const clearDay = go.renewal_day ? button("Clear", { variant: "ghost", size: "sm" }) : null;
  const put = (value) => async () => {
    try {
      await api.put("quota/opencode-go", { renewal_day: value() });
      toast(value() == null ? "Renewal day cleared." : "Renewal day saved.");
      await reload();
    } catch (error) { toast(error.message, "error"); }
  };
  save.addEventListener("click", () => busy(save, put(() => day.value === "" ? null : Number(day.value))));
  clearDay?.addEventListener("click", () => busy(clearDay, put(() => null)));
  return h("div", { class: "go-renewal" },
    h("label", { class: "small" }, h("span", { text: "Plan renews on day " }), day, h("span", { text: " of the month" })),
    save, clearDay,
    h("span", { class: "muted small", text: go.renewal_day
      ? "The monthly bar counts this billing cycle, starting at local midnight on that day."
      : "Without it, the monthly bar counts the last 30 days, which is not your billing cycle." }));
}

function accountManager(accounts, removed, reload) {
  const provider = select([["codex", "Codex / OpenAI"], ["claude_code", "Claude Code / Anthropic"]], "codex");
  const label = input({ placeholder: "Personal, work, second account…", required: true, maxlength: 60 });
  const directory = input({ placeholder: "~/.codex-second", required: true, maxlength: 1024 });
  const help = h("p", { class: "muted small" });
  const updateHelp = () => {
    directory.placeholder = provider.value === "codex" ? "~/.codex-second" : "~/.claude-second";
    help.textContent = (provider.value === "codex"
      ? "Use this directory as CODEX_HOME when signing in."
      : "Use this directory as CLAUDE_CONFIG_DIR when signing in.") +
      " On Windows, a profile inside WSL can be entered as \\\\wsl.localhost\\<distribution>\\home\\<you>\\… — the default one in each running distribution is listed automatically.";
  };
  provider.addEventListener("change", updateHelp);
  updateHelp();
  const add = button("Add account", { variant: "primary", type: "submit" });
  const form = h("form", { class: "stack account-add" },
    h("div", { class: "grid-2" },
      field("Agent", provider, { required: true }), field("Account name", label, { required: true })),
    field("Agent profile directory", directory, { required: true }), help, add);
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    await busy(add, async () => {
      try {
        await api.post("quota/accounts", { provider: provider.value, label: label.value, directory: directory.value });
        toast("Account added.");
        await reload(true);
      } catch (error) { toast(error.message, "error"); }
    });
  });

  const title = (account) => account.wsl ? `${names[account.provider]} · WSL ${account.wsl}` : names[account.provider];
  const accountRows = accounts.map(account => {
    const name = input({ value: account.label, maxlength: 60, "aria-label": `Name for ${title(account)} account` });
    const folder = input({ value: account.directory, maxlength: 1024, class: "input mono",
      "aria-label": `Agent profile directory for ${title(account)} account` });
    const save = button("Save", { size: "sm" });
    save.addEventListener("click", () => busy(save, async () => {
      try {
        await api.put(`quota/accounts/${encodeURIComponent(account.id)}`,
          { label: name.value, directory: folder.value.trim() === account.directory ? null : folder.value });
        toast("Account saved.");
        await reload();
      } catch (error) { toast(error.message, "error"); }
    }));
    const remove = button("Remove", { variant: "ghost", size: "sm" });
    remove.addEventListener("click", () => busy(remove, async () => {
      const confirmed = await confirmDialog({ title: `Stop monitoring ${account.label}?`,
        message: account.is_default
          ? "The agent profile and sign-in stay untouched. This detected account can be restored from Manage accounts."
          : "The agent profile and sign-in stay untouched.",
        confirmLabel: "Remove", danger: true });
      if (!confirmed) return;
      try { await api.del(`quota/accounts/${encodeURIComponent(account.id)}`); await reload(); }
      catch (error) { toast(error.message, "error"); }
    }));
    return h("div", { class: "account-manage-row" },
      h("div", { class: "account-manage-title" },
        h("strong", { text: title(account) }),
        h("span", { class: "muted small", text: account.is_default ? "Detected automatically" : "Added by you" })),
      h("div", { class: "account-manage-fields" }, name, folder),
      h("div", { class: "account-manage-actions" }, save, remove));
  });

  const removedRows = removed.map(account => {
    const restore = button("Restore", { size: "sm" });
    restore.addEventListener("click", () => busy(restore, async () => {
      try {
        await api.post(`quota/accounts/${encodeURIComponent(account.id)}/restore`, {});
        toast("Account restored.");
        await reload();
      } catch (error) { toast(error.message, "error"); }
    }));
    return h("div", { class: "account-manage-row account-removed-row" },
      h("div", { class: "account-manage-title" },
        h("strong", { text: `${title(account)} · ${account.label}` }),
        h("span", { class: "muted small mono truncate", text: account.directory || "Distribution not running" })),
      h("div", { class: "account-manage-actions" }, restore));
  });

  return h("details", { class: "card account-manager" },
    h("summary", null,
      h("div", null, h("strong", { text: "Manage accounts" }),
        h("span", { class: "muted small", text: `Rename, move, or remove ${accounts.length} monitored accounts, or add another` }))),
    h("div", { class: "account-manager-body" },
      h("div", { class: "account-manage-list" }, accountRows),
      removed.length ? [
        h("h3", { class: "card-title", text: "Removed accounts" }),
        h("div", { class: "account-manage-list" }, removedRows)
      ] : null,
      h("hr", { class: "divider" }),
      h("h3", { class: "card-title", text: "Add another account" }), form));
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
        if (report.contract_version !== "3") throw new Error("The broker and page use different quota contracts. Reload the page.");
        const providers = report.providers.filter(item => item.provider !== "opencode");
        mount(page,
          pageHead("Live quota", "Remaining account balances at a glance.", refresh),
          h("div", { class: "quota-grid" }, providers.map(providerCard)),
          report.open_code_go?.models?.length ? [
            h("div", { class: "section-heading" },
              h("div", null, h("h2", { text: "OpenCode Go" }),
                h("p", { class: "muted small", text: "Local estimates against published per-model caps and rates." })),
              h("a", { class: "small", href: "https://opencode.ai/docs/go/", target: "_blank", rel: "noreferrer", text: "How limits work ↗" })),
            goRenewal(report.open_code_go, load),
            h("div", { class: "quota-grid" }, report.open_code_go.models.map(goCard)),
            h("details", { class: "meta-disclosure page-disclosure" },
              h("summary", { text: "About the OpenCode estimate" }),
              h("p", { class: "muted small", text: `${report.open_code_go.message} The 5-hour and weekly windows roll backward from now; ${report.open_code_go.renewal_day ? "the monthly window follows your renewal day" : "set your renewal day to anchor the monthly window to your billing cycle"}. Rates checked ${report.open_code_go.pricing_as_of}.` }))
          ] : null,
          accountManager(configured.accounts, configured.removed ?? [], load),
          h("p", { class: "muted small page-footnote", text: "Live checks are cached for five minutes. Check now is limited to once every 30 seconds." }));
      } catch (error) {
        if (ctx.isCurrent()) mount(page, pageHead("Live quota", "Remaining account balances.", refresh), notice(error.message, "danger"));
      } finally { refresh.disabled = false; }
    };
    refresh.addEventListener("click", () => load(true));
    mount(page, pageHead("Live quota", "Checking account balances…", refresh), h("div", { class: "skeleton" }));
    await load();
  },
};
