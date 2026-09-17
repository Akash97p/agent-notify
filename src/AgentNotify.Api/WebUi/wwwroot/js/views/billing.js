import { api } from "../api.js";
import { h, mount, button, notice, field, input, select, busy, toast, confirmDialog, badge, checkbox } from "../dom.js";

const number = new Intl.NumberFormat(undefined, { maximumFractionDigits: 4 });
// "$0.56" rather than "0.56 USD"; an amount without a documented currency stays a plain number.
const money = (value, currency) => {
  if (!currency) return number.format(value);
  try {
    return new Intl.NumberFormat(undefined, { style: "currency", currency: currency.toUpperCase(),
      minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(value);
  } catch { return `${number.format(value)} ${currency}`; }
};
// The one balance each provider's card leads with; the rest stay under Account details.
const primaryBalance = { deepseek: "total", moonshot: "available", siliconflow: "total_balance", openrouter: "limit_remaining" };
const periodName = (period) => ({
  today: "Today",
  this_week: "This week",
  this_month: "This month",
  all_time: "All time",
  last_30_days: "Last 30 days",
  month_to_date: "Month to date",
}[period] || period);
const kindName = (kind) => kind.replace(/_/g, " ");

function snapshotCard(snapshot, displayName) {
  const ok = snapshot.status === "ok";
  const stale = ok && snapshot.message && snapshot.message.toLowerCase().includes("last successful");
  const statusBadge = ok
    ? badge(stale ? "Stale" : "Live", stale ? "warn" : "ok")
    : badge(snapshot.status === "unauthorized" ? "Key rejected"
      : snapshot.status === "rate_limited" ? "Rate limited" : "Unavailable",
      snapshot.status === "rate_limited" ? "warn" : "danger");
  const head = h("div", { class: "quota-account-head" },
    h("div", null,
      h("span", { class: "eyebrow", text: displayName || snapshot.provider }),
      h("h2", { class: "quota-account-name", text: snapshot.label })),
    statusBadge);
  if (!ok) {
    return h("section", { class: "card quota-account reveal" }, head,
      h("div", { class: "quota-account-body" },
        h("p", { class: "muted small", text: snapshot.message || "No balance is available." })));
  }
  const rows = [];
  if (stale) rows.push(h("p", { class: "compact-warning", text: snapshot.message }));
  if (snapshot.available === false)
    rows.push(h("p", { class: "compact-warning", text: "The provider reports this balance is too low for API calls." }));
  const balances = snapshot.balances || [];
  const lead = balances.filter(balance => balance.kind === primaryBalance[snapshot.provider]);
  const shown = lead.length ? lead : balances;
  const breakdown = lead.length ? balances.filter(balance => !lead.includes(balance)) : [];
  for (const balance of shown)
    rows.push(h("div", { class: "balance-row" },
      h("div", { class: "balance-head" },
        h("span", { class: "balance-label", text: balance.kind === "limit_remaining" ? "Limit remaining" : "Balance" }),
        h("strong", { class: "balance-value", text: money(balance.amount, balance.currency) }))));
  for (const entry of snapshot.spend || [])
    rows.push(h("div", { class: "balance-row" },
      h("div", { class: "balance-head" },
        h("span", { class: "balance-label", text: `Spent · ${periodName(entry.period)}` }),
        h("strong", { class: "balance-value", text: money(entry.amount, entry.currency) }))));
  const daily = snapshot.daily || [];
  if (daily.length) {
    const max = Math.max(0.0001, ...daily.map((day) => Number(day.amount) || 0));
    rows.push(h("div", { class: "balance-row" },
      h("span", { class: "balance-label", text: "Daily spend · last 30 days" }),
      h("div", { class: "stack" }, daily.map((day) =>
        h("div", { class: "balance-row" },
          h("div", { class: "balance-head" },
            h("span", { class: "muted small", text: day.date }),
            h("span", { class: "muted small", text: money(day.amount, day.currency) })),
          h("div", { class: "balance-track", role: "img", "aria-label": `${day.date}: ${money(day.amount, day.currency)}` },
            h("span", { class: "balance-fill balance-ok", style: { width: `${Math.max(2, (Number(day.amount) || 0) / max * 100)}%` } })))))));
  }
  rows.push(h("details", { class: "meta-disclosure" },
    h("summary", { text: "Account details" }),
    h("div", { class: "meta-lines" },
      breakdown.map(balance => h("span", { text: `${kindName(balance.kind)}: ${money(balance.amount, balance.currency)}` })),
      snapshot.fetched_at ? h("span", { text: `Checked: ${new Date(snapshot.fetched_at).toLocaleString()}` }) : null)));
  return h("section", { class: "card quota-account reveal" }, head,
    h("div", { class: "quota-account-body" }, rows));
}

function manager(accounts, providers, secretProtection, reload) {
  const byId = new Map(providers.map((p) => [p.id, p]));
  const provider = select(providers.map((p) => [p.id, p.display_name]), providers[0]?.id || "deepseek");
  const label = input({ placeholder: "Personal, work…", required: true, maxlength: 60 });
  const key = input({ type: "password", autocomplete: "off", required: true, placeholder: "Paste the provider key" });
  key.setAttribute("autocomplete", "off");
  const ack = checkbox("I understand the storage risk below.", false);
  const warning = notice("", "warn");
  const docs = h("a", { class: "small", target: "_blank", rel: "noreferrer" });
  const updateWarning = () => {
    const info = byId.get(provider.value) || { host: provider.value, display_name: provider.value };
    const admin = provider.value === "openai_admin" || provider.value === "anthropic_admin"
      ? " For OpenAI and Anthropic this is an Admin key, which can manage the whole organization."
      : "";
    warning.lastChild.textContent = `The key is stored encrypted (${secretProtection || "encrypted storage"}) for this user and is sent only to ${info.host}. Anyone who can run programs as this user could decrypt it. Prefer a dedicated key with the least access.${admin}`;
    docs.href = info.docs_url || "#";
    docs.textContent = info.docs_url ? `How to create a ${info.display_name} key ↗` : "";
  };
  provider.addEventListener("change", updateWarning);
  updateWarning();
  const add = button("Add account", { variant: "primary", type: "submit" });
  const form = h("form", { class: "stack account-add" },
    h("div", { class: "grid-2" },
      field("Provider", provider, { required: true }),
      field("Account name", label, { required: true })),
    docs,
    h("div", null, warning),
    field("API key", key, { required: true }),
    ack, add);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    if (!ack.input.checked) {
      toast("Tick “I understand” to store the key.", "error");
      return;
    }
    busy(add, async () => {
      try {
        await api.post("billing/accounts", {
          provider: provider.value, label: label.value, api_key: key.value, acknowledge_risk: true,
        });
        toast("Account added.");
        label.value = "";
        await reload();
      } catch (error) { toast(error.message, "error"); }
      finally { key.value = ""; }
    });
  });

  const rows = accounts.map((account) => {
    const info = byId.get(account.provider) || { display_name: account.provider };
    const name = input({ value: account.label, maxlength: 60, "aria-label": `Name for ${account.label}` });
    const replacement = input({ type: "password", autocomplete: "off", placeholder: "Leave blank to keep the key",
      "aria-label": `New key for ${account.label}` });
    replacement.setAttribute("autocomplete", "off");
    const save = button("Save", { size: "sm" });
    save.addEventListener("click", () => busy(save, async () => {
      try {
        const body = replacement.value
          ? { label: name.value, api_key: replacement.value }
          : { label: name.value };
        await api.put(`billing/accounts/${encodeURIComponent(account.id)}`, body);
        toast("Account saved.");
        await reload();
      } catch (error) { toast(error.message, "error"); }
      finally { replacement.value = ""; }
    }));
    const remove = button("Remove", { variant: "ghost", size: "sm" });
    remove.addEventListener("click", () => busy(remove, async () => {
      const confirmed = await confirmDialog({ title: `Remove ${account.label}?`,
        message: "The stored key is deleted. The provider account itself is untouched.",
        confirmLabel: "Remove", danger: true });
      if (!confirmed) return;
      try { await api.del(`billing/accounts/${encodeURIComponent(account.id)}`); await reload(); }
      catch (error) { toast(error.message, "error"); }
    }));
    return h("div", { class: "account-manage-row" },
      h("div", { class: "account-manage-title" },
        h("strong", { text: info.display_name }),
        h("span", { class: "muted small", text: "Key stored · never shown" })),
      h("div", { class: "account-manage-fields" }, name, replacement),
      h("div", { class: "account-manage-actions" }, save, remove));
  });

  return h("details", { class: "card account-manager" },
    h("summary", null,
      h("div", null, h("strong", { text: "Manage API accounts" }),
        h("span", { class: "muted small", text: `Rename, replace keys, or remove ${accounts.length} stored accounts` }))),
    h("div", { class: "account-manager-body" },
      h("div", { class: "account-manage-list" }, rows.length ? rows
        : h("p", { class: "muted small", text: "No API accounts stored." })),
      h("hr", { class: "divider" }),
      h("h3", { class: "card-title", text: "Add an API account" }), form));
}

// Returns at once: provider calls can take seconds and must not hold up the rest of Live quota.
// The balance cards and the account manager are separate so the page can show balances first.
export default function renderBilling() {
  const cards = h("div", { class: "stack" });
  const host = h("div", { class: "stack" });
  const load = async () => {
    try {
      const [report, listed, overview] = await Promise.all([
        api.get("billing"), api.get("billing/accounts"), api.get("overview").catch(() => null)]);
      if (report.contract_version !== "1") throw new Error("The broker and page use different billing contracts. Reload the page.");
      const names = new Map((listed.providers || []).map((p) => [p.id, p.display_name]));
      const refresh = button("Check now", { iconName: "refresh" });
      refresh.addEventListener("click", () => busy(refresh, async () => {
        try {
          await api.post("billing/refresh");
          await load();
        } catch (error) { toast(error.message, "error"); }
      }));
      mount(cards, report.accounts?.length ? [
        h("div", { class: "section-heading" },
          h("div", null, h("h2", { text: "API accounts" }),
            h("p", { class: "muted small", text: "Provider balances and spend from official APIs. Keys stay on the broker." })),
          refresh),
        h("div", { class: "quota-grid" },
          report.accounts.map((snapshot) => snapshotCard(snapshot, names.get(snapshot.provider))))
      ] : null);
      mount(host,
        report.accounts?.length ? null
          : notice("No API accounts yet. Add one below to see its balance or spend.", "info"),
        manager(listed.accounts || [], listed.providers || [], overview?.secret_protection || "", load),
        h("p", { class: "muted small page-footnote", text: "Snapshots are cached for five minutes. Check now is limited to once every 60 seconds per account. Meta, Z.ai, Xiaomi MiMo, Google Gemini, and xAI have no usable official endpoint and are not listed." }));
    } catch (error) {
      mount(cards, null);
      mount(host, notice(error.message, "danger"));
    }
  };
  mount(cards, h("div", { class: "skeleton" }));
  load();
  return { cards, manager: host };
}
