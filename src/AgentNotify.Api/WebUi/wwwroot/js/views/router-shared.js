import { api } from "../api.js";
import {
  h, mount, clear, pageHead, badge, button, busy, empty, toast, card, notice,
  field, input, select, checkbox, toggle, confirmDialog,
} from "../dom.js";

// Page titles live here so every section shares one voice.
const TITLES = {
  providers: ["Providers", "Add a provider, tick its models, and they show up in your agents' model pickers."],
  routing: ["Routing", "Smart routing, plus optional nicknames and fallback chains on top of your providers' models."],
  agents: ["Agents", "Point an agent's own model picker at the router, and put its settings back."],
  activity: ["Activity", "What the router actually sent, per request and per attempt."],
};

function offBanner() {
  return notice("The router is off, so no request reaches any model provider. Turn it on under Providers.", "info");
}

const wireOptions = [
  ["openai_responses", "openai_responses"],
  ["openai_chat", "openai_chat"],
  ["anthropic_messages", "anthropic_messages"],
];

function fmtTime(value) {
  if (!value) return "";
  try { return new Date(value).toLocaleString(); } catch { return String(value); }
}

function copyBlock(text) {
  const pre = h("pre", { class: "code-text", style: { whiteSpace: "pre-wrap", margin: "0", fontFamily: "inherit", overflowWrap: "anywhere" }, text });
  const copy = button("", { variant: "ghost", size: "sm", iconName: "copy", title: "Copy" });
  copy.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(text);
      toast("Copied to the clipboard.");
    } catch {
      toast("Copy failed. Select the text instead.", "error");
    }
  });
  return h("div", { class: "code" }, pre, copy);
}

function outcomeBadge(outcome) {
  if (outcome === "ok") return badge("ok", "ok");
  if (outcome === "upstream_error" || outcome === "failed_over_exhausted") return badge(outcome.replace(/_/g, " "), "danger");
  if (outcome === "canceled") return badge("canceled", "warn");
  if (outcome === "client_error") return badge("client error", "warn");
  return outcome ? badge(outcome.replace(/_/g, " ")) : badge("pending");
}

/**
 * One implementation behind the Model router pages. Each page renders the same live state and shows
 * the section it owns, so the on/off switch and base URLs stay in view wherever you are.
 */
export async function renderRouter(page, ctx, section) {
    let data;
    try {
      data = await api.get("router");
    } catch (error) {
      mount(page, pageHead(TITLES[section][0], TITLES[section][1]), notice(error.message, "danger"));
      return;
    }

    let state = data;
    let routeEditId = null;
    let requests = [];
    let summary = [];
    let summaryDays = 1;

    const agentsHost = h("div");
    const statusHost = h("div");
    const connectHost = h("div");
    const upstreamsHost = h("div");
    const routesHost = h("div");
    const smartHost = h("div");
    const defaultHost = h("div");
    const ledgerHost = h("div");
    const summaryHost = h("div");

    const reload = async () => {
      try {
        state = await api.get("router");
        drawStatus();
        drawConnect();
        drawUpstreams();
        drawSmart();
        drawRoutes();
        drawDefault();
      } catch (error) {
        toast(error.message, "error");
      }
    };

    // ---- status card -----------------------------------------------------------------

    const keyRevealHost = h("div", { class: "stack" });

    function showKeyOnce(key) {
      clear(keyRevealHost);
      keyRevealHost.append(
        notice("This key is shown only once. Copy it now and store it securely.", "warn"),
        copyBlock(key),
      );
    }

    function drawStatus() {
      const enabledToggle = toggle(state.enabled ? "Router on" : "Router off", state.enabled,
        { help: state.enabled ? "Agents you connect send their model requests through AgentNotify." : "Nothing is sent to any model provider until you turn it on." });

      const regen = button("Regenerate key", { size: "sm", iconName: "refresh" });
      regen.addEventListener("click", () => busy(regen, async () => {
        const ok = await confirmDialog({
          title: "Regenerate router key?",
          message: "Connected agents are updated automatically. Anything you configured by hand stops working until you paste the new key.",
          confirmLabel: "Regenerate",
          danger: true,
        });
        if (!ok) return;
        try {
          const result = await api.post("router/key/regenerate", {});
          showKeyOnce(result.key);
          toast("Router key regenerated.");
          state.has_key = true;
        } catch (error) {
          toast(error.message, "error");
        }
      }));

      enabledToggle.input.addEventListener("change", () => busy(enabledToggle.input, async () => {
        const desired = enabledToggle.input.checked;
        try {
          const result = await api.post("router/enable", { enabled: desired });
          state.enabled = result.enabled;
          toast(desired ? "Router on." : "Router off.");
          await reload();
          // Reloading redraws this card, so a freshly generated key is shown after it.
          if (result.key) showKeyOnce(result.key);
        } catch (error) {
          enabledToggle.input.checked = !desired;
          toast(error.message, "error");
        }
      }));

      mount(statusHost,
        card({
          body: h("div", { class: "fields" },
            enabledToggle,
            keyRevealHost,
            h("details", { class: "disclosure" },
              h("summary", { text: "Connection details for configuring an agent by hand" }),
              h("div", { class: "stack" },
                field("Responses and Chat base URL", copyBlock(state.base_url || "")),
                field("Anthropic base URL", copyBlock(state.anthropic_base_url || "")),
                h("p", { class: "muted small", text: state.has_key ? "A router key is stored. Connecting an agent on the Agents page writes it for you." : "No router key yet. Turn the router on to generate one." }),
                h("div", { class: "row" }, regen))),
          ),
        }),
      );
    }

    // ---- providers -------------------------------------------------------------------

    const KIND_LABEL = { subscription: "Subscription", api: "Pay per token", local: "This computer" };
    const WIRE_LABEL = { openai_responses: "Responses API", openai_chat: "Chat Completions", anthropic_messages: "Messages API" };

    // What the editor is showing: a preset being added ("custom" for none), or an existing upstream.
    let adding = null;

    function presetFor(upstream) {
      return state.presets.find(p => p.id === upstream.slug && p.auth === upstream.auth)
        || state.presets.find(p => p.base_url === upstream.base_url && p.auth === upstream.auth)
        || null;
    }

    function hostOf(url) {
      try { return new URL(url).host; } catch { return url; }
    }

    function freeSlug(base) {
      const taken = new Set(state.upstreams.map(u => u.slug));
      if (!taken.has(base)) return base;
      for (let i = 2; i < 100; i++) if (!taken.has(`${base}-${i}`)) return `${base}-${i}`;
      return base;
    }

    function providerRow(u) {
      const preset = presetFor(u);
      const kind = u.auth !== "api_key" ? "subscription" : preset?.kind || "api";
      const onSwitch = toggle("", u.enabled);
      onSwitch.title = u.enabled ? "On — click to turn off" : "Off — click to turn on";
      onSwitch.addEventListener("click", event => event.stopPropagation());
      onSwitch.input.addEventListener("change", () => busy(onSwitch.input, async () => {
        try {
          await api.put(`router/upstreams/${encodeURIComponent(u.id)}`, {
            slug: u.slug, label: u.label, wire: u.wire, base_url: u.base_url, models: u.models,
            enabled: onSwitch.input.checked,
          });
          await reload();
        } catch (error) {
          onSwitch.input.checked = !onSwitch.input.checked;
          toast(error.message, "error");
        }
      }));
      const count = u.models?.length || 0;
      return h("div", { class: "provider-row", "aria-selected": String(adding?.upstream?.id === u.id) },
        h("button", { type: "button", class: "provider-row-main", onClick: () => { adding = { upstream: u, preset }; drawUpstreams(); } },
          h("div", { class: "list-title", text: u.label }),
          h("div", { class: "list-sub", text: `${KIND_LABEL[kind]} · ${count} model${count === 1 ? "" : "s"} · ${u.slug}/…` })),
        h("div", { class: "provider-row-side" },
          count === 0 ? badge("No models", "warn") : null,
          u.credential_ref?.startsWith("api_account:") ? badge("Key from API accounts", "info")
            : u.auth === "api_key" && preset?.needs_key && !u.has_key ? badge("No key", "danger") : null,
          onSwitch),
      );
    }

    function presetTile(p) {
      const added = p.auth === "codex_chatgpt"
        ? state.upstreams.filter(u => u.auth === p.auth).length >= Math.max(1, (state.accounts?.codex_accounts || []).length)
        : state.upstreams.some(u => (u.slug === p.id || u.base_url === p.base_url) && u.auth === p.auth);
      const chips = [];
      if (added) chips.push(badge("Added", "ok"));
      const saved = (state.accounts?.api_accounts || []).filter(a => a.preset_id === p.id);
      if (saved.length) chips.push(badge("Key in API accounts", "info"));
      else if (p.opencode_key) chips.push(badge("Key found in OpenCode", "info"));
      if (p.auth === "codex_chatgpt") {
        const accounts = state.accounts?.codex_accounts || [];
        const used = state.upstreams.filter(u => u.auth === "codex_chatgpt").length;
        if (accounts.length > 1) chips.push(badge(`${accounts.length} Codex accounts${used ? ` · ${used} added` : ""}`, "info"));
      }
      if (p.auth === "muse_code") chips.push(p.signin_ready ? badge("Signed in", "ok") : badge("Not signed in", "warn"));
      else if (p.auth === "codex_chatgpt") chips.push((state.accounts?.codex_accounts || []).some(a => a.signed_in) ? badge("Signed in", "ok") : badge("Not signed in", "warn"));
      if (p.unofficial) chips.push(badge("Unofficial", "warn"));
      return h("button", { type: "button", class: "provider-tile", onClick: () => { adding = { preset: p }; drawUpstreams(); } },
        h("div", { class: "provider-tile-name", text: p.display_name }),
        h("div", { class: "muted small", text: p.blurb || hostOf(p.base_url) }),
        chips.length ? h("div", { class: "row provider-tile-chips" }, chips) : null);
    }

    function drawUpstreams() {
      const rows = state.upstreams.map(providerRow);
      const yours = state.upstreams.length
        ? card({
            title: "Your providers",
            description: "Every model ticked here is already in your connected agents' model pickers as provider/model. No route needed.",
            body: h("div", { class: "provider-list" }, rows),
          })
        : null;

      let lower;
      if (adding) {
        lower = buildProviderEditor(adding.preset || null, adding.upstream || null);
      } else {
        const groups = [
          ["subscription", "Subscriptions", "A monthly plan you already pay for."],
          ["api", "Pay per token", "An API key billed by usage."],
          ["local", "On this computer", "No key and no internet needed."],
        ];
        lower = card({
          title: state.upstreams.length ? "Add another provider" : "Add a provider",
          description: "Pick one. You'll paste a key (or reuse a sign-in), tick the models you want, and save.",
          body: h("div", { class: "stack" },
            groups.map(([kind, title, text]) => {
              const presets = state.presets.filter(p => p.kind === kind);
              if (!presets.length) return null;
              return h("div", { class: "stack-sm" },
                h("div", null, h("h3", { class: "provider-group-title", text: title }), h("p", { class: "muted small", text })),
                h("div", { class: "provider-grid" }, presets.map(presetTile)));
            }),
            h("div", { class: "row" },
              button("Custom provider", { size: "sm", iconName: "plus", onClick: () => { adding = { preset: null }; drawUpstreams(); } }),
              h("span", { class: "muted small", text: "Any OpenAI- or Anthropic-compatible endpoint." }))),
        });
      }

      mount(upstreamsHost, h("div", { class: "page-stack" }, yours, lower));
    }

    function buildProviderEditor(preset, existing) {
      const isEdit = !!existing;
      const custom = !preset;
      const auth = existing?.auth || preset?.auth || "api_key";
      const subscription = auth !== "api_key";
      const baseWire = () => wire.value;

      // Connection details. A preset fills them all, so they sit under "Advanced".
      const slug = input({ value: existing?.slug || freeSlug(preset?.id || ""), maxlength: 32, placeholder: "my-provider", autocomplete: "off", spellcheck: "false" });
      const label = input({ value: existing?.label || preset?.display_name || "", maxlength: 60, placeholder: "My provider", autocomplete: "off" });
      const wire = select(wireOptions.map(([v]) => [v, `${WIRE_LABEL[v]} (${v})`]), existing?.wire || preset?.wire || "openai_chat");
      const baseUrl = input({ value: existing?.base_url || preset?.base_url || "", placeholder: "https://api.example.com/v1", autocomplete: "off", spellcheck: "false" });
      const enabled = toggle("On", existing ? existing.enabled : true);
      const status = h("div", { class: "stack" });

      // Where the key comes from. A key already known elsewhere in AgentNotify (API accounts) is
      // referenced, so it is entered and rotated in one place; OpenCode's is copied; one can be typed.
      const needsKeyUi = !subscription && (custom || preset?.needs_key || existing?.has_key || existing?.credential_ref);
      const apiAccounts = (state.accounts?.api_accounts || []).filter(a => preset && a.preset_id === preset.id);
      const sourceOptions = [];
      if (existing?.credential_ref?.startsWith("api_account:")) {
        const current = (state.accounts?.api_accounts || []).find(a => a.credential_ref === existing.credential_ref);
        sourceOptions.push(["keep", current ? `API account · ${current.label} (in use)` : "API account (removed — choose another)"]);
      } else if (existing?.has_key) {
        sourceOptions.push(["keep", "Keep the stored key"]);
      }
      for (const account of apiAccounts)
        if (account.credential_ref !== existing?.credential_ref)
          sourceOptions.push([account.credential_ref, `API account · ${account.label}`]);
      if (preset?.opencode_key) sourceOptions.push(["opencode", "The key OpenCode already has"]);
      sourceOptions.push(["paste", preset?.needs_key === false ? "Paste a key (optional)" : "Paste a key"]);
      const keySource = select(sourceOptions, sourceOptions[0][0]);
      const apiKey = input({ type: "password", autocomplete: "off", spellcheck: "false", placeholder: "Paste the key" });
      const clearKey = isEdit && existing.has_key ? checkbox("Remove the stored key", false) : null;
      clearKey?.input.addEventListener("change", () => { apiKey.disabled = clearKey.input.checked; if (clearKey.input.checked) apiKey.value = ""; });
      const keyHelp = h("p", { class: "field-help" });
      const keyField = field("API key", apiKey, {
        extra: preset?.key_url ? h("a", { href: preset.key_url, target: "_blank", rel: "noopener noreferrer", class: "small", text: "Get a key ↗" }) : null,
      });
      const syncKeySource = () => {
        const source = keySource.value;
        keyField.hidden = source !== "paste";
        const host = hostOf(baseUrl.value || preset?.base_url || "the base URL");
        keyHelp.textContent = source.startsWith("api_account:") || (source === "keep" && existing?.credential_ref)
          ? "Uses the key saved under Live quota → API accounts. Change it there and this provider follows."
          : source === "opencode" ? `Copied from OpenCode into AgentNotify's encrypted store; sent only to ${host}.`
          : `Stored encrypted for your user and sent only to ${host}.`;
      };
      keySource.addEventListener("change", () => { syncKeySource(); if (keySource.value !== "paste") fetchModels(); });
      syncKeySource();
      const keyBlock = needsKeyUi
        ? h("div", { class: "stack-sm" }, sourceOptions.length > 1 ? field("Key", keySource) : null, keyField, keyHelp)
        : null;

      // A ChatGPT plan covers every Codex account this computer lists (the ones Insights shows): adding
      // it adds one provider per signed-in account, so the owner never picks one. Each provider still
      // belongs to one account, shown rather than chosen.
      const codexAccounts = auth === "codex_chatgpt" ? (state.accounts?.codex_accounts || []) : [];
      const usedProfiles = new Set(state.upstreams.filter(u => u.auth === "codex_chatgpt" && u.id !== existing?.id)
        .map(u => u.credential_ref || codexAccounts.find(a => a.is_default)?.credential_ref));
      const currentProfile = existing
        ? existing.credential_ref || codexAccounts.find(a => a.is_default)?.credential_ref
        : (codexAccounts.find(a => !usedProfiles.has(a.credential_ref)) || codexAccounts[0])?.credential_ref;
      const selectedCodex = () => codexAccounts.find(a => a.credential_ref === currentProfile);
      const signinNotice = h("div");
      const syncCodexAccount = () => {
        const account = selectedCodex();
        if (!account) return;
        const others = codexAccounts.filter(a => a.credential_ref !== account.credential_ref && a.signed_in);
        const text = isEdit
          ? `${account.label} · ${account.display_directory || account.directory}. ${account.detail}`
          : `${account.detail} ${others.length ? `Your other Codex account${others.length > 1 ? "s" : ""} (${others.map(a => a.display_directory || a.directory).join(", ")}) ${others.length > 1 ? "are" : "is"} added as ${others.length > 1 ? "their own providers" : "its own provider"} too, so smart routing can move a model from one plan to the next.` : ""}`;
        mount(signinNotice, notice(text, account.signed_in ? "ok" : "danger"));
        if (!isEdit) {
          const suffix = account.is_default ? "" : "-" + (account.label || account.id).toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 20);
          slug.value = freeSlug("chatgpt" + suffix);
          label.value = account.is_default ? "ChatGPT plan" : `ChatGPT plan · ${account.label}`;
        }
      };

      const credentialRef = () => {
        if (auth === "codex_chatgpt") return selectedCodex()?.credential_ref || null;
        const source = keySource.value;
        return source.startsWith("api_account:") ? source : null;
      };

      // Models: fetched from the provider and ticked, plus any typed by hand.
      const known = new Map(); // id -> wire
      const selected = new Set(existing?.models || []);
      for (const m of existing?.models || []) known.set(m, existing.model_wires?.[m] || existing.wire);
      const modelHost = h("div", { class: "model-picker" });
      const modelStatus = h("p", { class: "muted small" });
      const search = input({ type: "search", placeholder: "Filter models", autocomplete: "off", spellcheck: "false" });
      const manual = input({ placeholder: "Add a model ID by hand", autocomplete: "off", spellcheck: "false" });
      const counter = h("span", { class: "muted small" });
      let fetched = false;

      const drawModels = () => {
        const term = search.value.trim().toLowerCase();
        const ids = [...known.keys()];
        const shown = term ? ids.filter(id => id.toLowerCase().includes(term)) : ids;
        counter.textContent = `${selected.size} of ${ids.length} selected`;
        if (!ids.length) {
          mount(modelHost, h("p", { class: "muted small model-empty", text: fetched ? "This provider listed no models. Add one by ID below." : "Models appear here once the provider can be asked." }));
          return;
        }
        mount(modelHost, shown.map(id => {
          const box = h("input", { type: "checkbox", checked: selected.has(id) });
          box.addEventListener("change", () => { if (box.checked) selected.add(id); else selected.delete(id); counter.textContent = `${selected.size} of ${ids.length} selected`; });
          const modelWire = known.get(id);
          return h("label", { class: "model-option" }, box,
            h("span", { class: "mono small", text: id }),
            modelWire && modelWire !== baseWire() ? h("span", { class: "model-wire", text: WIRE_LABEL[modelWire] || modelWire }) : null);
        }));
      };
      search.addEventListener("input", drawModels);

      const selectAll = button("Select all", { size: "sm", onClick: () => { for (const id of known.keys()) if (!search.value || id.toLowerCase().includes(search.value.toLowerCase())) selected.add(id); drawModels(); } });
      const selectNone = button("Clear", { size: "sm", onClick: () => { selected.clear(); drawModels(); } });
      const addManual = button("Add", { size: "sm", iconName: "plus" });
      const doAddManual = () => {
        const id = manual.value.trim();
        if (!id) return;
        if (!known.has(id)) known.set(id, baseWire());
        selected.add(id);
        manual.value = "";
        drawModels();
      };
      addManual.addEventListener("click", doAddManual);
      manual.addEventListener("keydown", event => { if (event.key === "Enter") { event.preventDefault(); doAddManual(); } });

      const refresh = button("Fetch models", { size: "sm", iconName: "refresh" });
      async function fetchModels() {
        const body = {
          preset_id: preset?.id || null,
          upstream_id: existing?.id || null,
          base_url: baseUrl.value.trim() || null,
          wire: wire.value,
          auth,
          api_key: keySource.value === "paste" ? apiKey.value || null : null,
          use_opencode_key: keySource.value === "opencode",
          credential_ref: credentialRef(),
        };
        modelStatus.textContent = "Asking the provider for its models…";
        modelStatus.className = "muted small";
        try {
          const result = await api.post("router/models/fetch", body);
          const firstFetch = !fetched && !isEdit;
          fetched = true;
          for (const m of result.models) known.set(m.id, m.wire);
          // A short list is ticked for you; a long one (OpenRouter lists hundreds) is left to choose from.
          if (firstFetch && result.models.length <= 40) for (const m of result.models) selected.add(m.id);
          modelStatus.textContent = result.models.length
            ? `${result.models.length} models available.${firstFetch && result.models.length > 40 ? " Tick the ones you want." : ""}`
            : "The provider listed no models.";
        } catch (error) {
          modelStatus.textContent = error.message;
          modelStatus.className = "small text-danger";
        }
        drawModels();
      }
      refresh.addEventListener("click", () => busy(refresh, fetchModels));
      // Asking needs a key for most providers: ask as soon as one is pasted.
      apiKey.addEventListener("change", () => { if (apiKey.value && keySource.value === "paste") fetchModels(); });

      // Ask straight away whenever no key has to be typed first.
      syncCodexAccount();
      const canAskNow = subscription
        ? (auth === "codex_chatgpt" ? selectedCodex()?.signed_in !== false : preset?.signin_ready !== false)
        : keySource.value !== "paste" || (!custom && preset?.needs_key === false);
      drawModels();
      if (canAskNow) queueMicrotask(fetchModels);
      else modelStatus.textContent = subscription ? "" : "Paste the key, and the model list loads by itself.";

      // Save.
      const save = button(isEdit ? "Save changes" : "Add provider", { variant: "primary", iconName: "check" });
      save.addEventListener("click", () => busy(save, async () => {
        const models = [...known.keys()].filter(id => selected.has(id));
        if (!models.length) {
          status.replaceChildren(notice("Tick at least one model, or add one by ID.", "danger"));
          return;
        }
        const modelWires = {};
        for (const id of models) { const w = known.get(id); if (w && w !== wire.value) modelWires[id] = w; }
        const body = {
          preset_id: preset?.id || null,
          slug: slug.value.trim(),
          label: label.value.trim(),
          wire: wire.value,
          base_url: baseUrl.value.trim(),
          auth,
          api_key: keySource.value === "paste" ? apiKey.value || null : null,
          use_opencode_key: keySource.value === "opencode",
          // Omitted when keeping what is stored; otherwise the reference, or "" to drop an old one.
          credential_ref: keySource.value === "keep" && auth === "api_key" ? null : credentialRef() ?? "",
          // The statement next to the key field is the acknowledgement.
          ack_key_storage: true,
          clear_key: clearKey?.input.checked || false,
          models,
          model_wires: modelWires,
          enabled: enabled.input.checked,
        };
        try {
          if (isEdit) await api.put(`router/upstreams/${encodeURIComponent(existing.id)}`, body);
          else await api.post("router/upstreams", body);
          toast(isEdit ? "Provider saved." : `${body.label} added. Its models are in your agents' pickers as ${body.slug}/…`);
          adding = null;
          await reload();
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      const remove = isEdit ? button("Delete", { variant: "danger", iconName: "trash" }) : null;
      remove?.addEventListener("click", async () => {
        const ok = await confirmDialog({ title: `Delete ${existing.label}?`, message: "Its models disappear from your agents' pickers.", confirmLabel: "Delete provider", danger: true });
        if (!ok) return;
        await busy(remove, async () => {
          try {
            await api.del(`router/upstreams/${encodeURIComponent(existing.id)}`);
            toast("Provider deleted.");
            adding = null;
            await reload();
          } catch (error) {
            status.replaceChildren(notice(error.message, "danger"));
          }
        });
      });
      const cancel = button("Cancel", { onClick: () => { adding = null; drawUpstreams(); } });

      const intro = [];
      if (preset?.unofficial)
        intro.push(notice(`Unofficial. This reuses the sign-in ${auth === "codex_chatgpt" ? "Codex" : "Muse Code"} keeps on this computer; the plan does not document use from other apps, so use it at your own risk. AgentNotify stores no key for it.`, "warn"));
      if (auth === "codex_chatgpt")
        intro.push(signinNotice);
      else if (subscription)
        intro.push(notice(preset?.signin_detail || "Uses this computer's sign-in.", preset?.signin_ready === false ? "danger" : "ok"));

      return card({
        title: isEdit ? existing.label : custom ? "Custom provider" : preset.display_name,
        description: isEdit ? `${existing.slug}/… · ${hostOf(existing.base_url)}` : custom ? "Any endpoint that speaks the OpenAI or Anthropic API." : preset.blurb || hostOf(preset.base_url),
        body: [
          intro,
          custom ? h("div", { class: "grid-2" }, field("Name", label, { required: true }), field("Base URL", baseUrl, { required: true, help: "https, or http for a server on this computer." })) : null,
          custom ? field("API format", wire, { help: "Most providers use Chat Completions." }) : null,
          keyBlock,
          h("div", { class: "field" },
            h("div", { class: "row-between" }, h("span", { class: "field-label", text: "Models" }), h("div", { class: "row" }, counter, refresh)),
            modelStatus,
            h("div", { class: "row" }, search, selectAll, selectNone),
            modelHost,
            h("div", { class: "row" }, manual, addManual)),
          h("details", { class: "disclosure" },
            h("summary", { text: "Advanced" }),
            h("div", { class: "fields" },
              h("div", { class: "grid-2" },
                field("Slug", slug, { required: true, help: "The provider part of provider/model. Lower-case letters, digits, hyphens." }),
                custom ? null : field("Name", label, { required: true })),
              custom ? null : h("div", { class: "grid-2" }, field("API format", wire), field("Base URL", baseUrl, { help: "https, or http for a server on this computer." })),
              enabled,
              clearKey)),
          status,
        ],
        footer: [remove, h("span", { class: "grow" }), cancel, save],
      });
    }

    // ---- configuring an agent by hand ----------------------------------------------------

    function drawConnect() {
      const p = state.base_url || "http://127.0.0.1:PORT/router/v1";
      const anthBase = state.anthropic_base_url || "http://127.0.0.1:PORT/router";
      const codexBlock = `model_provider = "agentnotify"\nmodel = "deepseek/deepseek-chat"\n\n[model_providers.agentnotify]\nname = "AgentNotify router"\nbase_url = "${p}"\nenv_key = "AGENTNOTIFY_ROUTER_KEY"\nwire_api = "responses"`;
      const claudeBlock = `export ANTHROPIC_BASE_URL=${anthBase}\nexport ANTHROPIC_CUSTOM_HEADERS="x-agentnotify-router-key: $(agentnotify router key)"\nexport ANTHROPIC_MODEL=deepseek/deepseek-chat`;

      mount(connectHost,
        h("details", { class: "disclosure" },
          h("summary", { text: "Rather configure an agent by hand?" }),
          h("div", { class: "stack" },
            h("p", { class: "muted small", text: "Connect above edits the agent's own files for you and keeps a copy. To do it yourself instead, use these, with any provider/model in place of the example." }),
            h("h3", { class: "card-title", text: "Codex (~/.codex/config.toml)" }),
            copyBlock(codexBlock),
            h("h3", { class: "card-title", text: "Claude Code (environment)" }),
            copyBlock(claudeBlock),
            h("p", { class: "muted small", text: "AGENTNOTIFY_ROUTER_KEY is the output of 'agentnotify router key'. Restart the agent after changing its configuration." }))),
      );
    }

    // ---- routes: nicknames and fallback chains ---------------------------------------

    const ROUTE_KIND = {
      alias: ["Nickname", "A short name for one model."],
      combo: ["Fallback chain", "Tries each model in order until one answers."],
    };

    /** Every provider/model, grouped by provider, for a select. */
    function modelChoices() {
      return state.upstreams.filter(u => u.models?.length).map(u => ({
        label: u.label,
        options: u.models.map(m => `${u.slug}/${m}`),
      }));
    }

    function modelPicker(value, { allowEmpty = false, emptyLabel = "Choose a model" } = {}) {
      const el = h("select", { class: "select" });
      if (allowEmpty || !value) el.append(h("option", { value: "", text: emptyLabel }));
      let found = !value;
      for (const group of modelChoices()) {
        const og = h("optgroup", { label: group.label });
        for (const option of group.options) {
          if (option === value) found = true;
          og.append(h("option", { value: option, text: option, selected: option === value }));
        }
        el.append(og);
      }
      // A route may name a model its provider no longer lists; keep it visible rather than losing it.
      if (!found) el.append(h("option", { value, text: `${value} (not in a provider's list)`, selected: true }));
      return el;
    }

    function routeCard(r) {
      const isSelected = r.id === routeEditId;
      return h("button", {
        type: "button",
        class: "list-item",
        "aria-selected": String(isSelected),
        onClick: () => { routeEditId = r.id; drawRoutes(); },
      },
        h("div", { class: "list-main" },
          h("div", { class: "list-title", text: r.name }),
          h("div", { class: "list-sub", text: ROUTE_KIND[r.kind]?.[0] || r.kind }),
          // One model per line: a chain of provider/model names never fits on one.
          h("ol", { class: "route-chain" }, (r.targets || []).map(t => h("li", { text: t })))),
        h("div", { class: "list-side" }, r.enabled ? badge("On", "ok") : badge("Off")),
      );
    }

    function drawRoutes() {
      const listBody = h("div", { class: "list" });
      if (!state.routes.length) {
        listBody.append(empty("No routes", "That's fine — routes are optional.", "route"));
      } else {
        for (const r of state.routes) listBody.append(routeCard(r));
      }

      const editing = routeEditId ? state.routes.find(x => x.id === routeEditId) : null;
      const addBtn = button("New route", { variant: routeEditId ? null : "primary", size: "sm", iconName: "plus", onClick: () => { routeEditId = null; drawRoutes(); } });

      mount(routesHost,
        notice("You don't need a route to use a model. Every model you ticked on the Providers page is already in your agents' pickers as provider/model — for example opencode-go/kimi-k3. "
          + "A route only adds a Nickname (a short name for one model) or a Fallback chain (several models tried in order, so a rate-limited or failing provider hands over to the next; this was called a combo).", "info"),
        h("div", { class: "section-heading" },
          h("div", null, h("h2", { text: "Routes" }), h("p", { class: "muted small", text: "Nicknames and fallback chains." })),
          addBtn),
        h("div", { class: "split" },
          h("div", { class: "sticky" }, card({ title: "Routes", body: listBody })),
          h("div", null, buildRouteForm(editing))),
      );
    }

    function buildRouteForm(existing) {
      const isEdit = !!existing;
      const name = input({ value: existing?.name || "", maxlength: 64, placeholder: "fast", autocomplete: "off", spellcheck: "false" });
      const kind = select(Object.entries(ROUTE_KIND).map(([id, [title, text]]) => [id, `${title} — ${text}`]), existing?.kind || "alias");
      const enabled = toggle("On", existing ? existing.enabled : true);
      const status = h("div", { class: "stack" });

      let targets = (existing?.targets || []).slice();
      if (!targets.length) targets = [""];
      const targetsHost = h("div", { class: "stack" });

      const rebuildTargets = () => {
        if (kind.value === "alias" && targets.length > 1) targets = [targets[0]];
        clear(targetsHost);
        const chain = kind.value === "combo";
        targets.forEach((t, idx) => {
          const picker = modelPicker(t);
          picker.addEventListener("change", () => { targets[idx] = picker.value; });
          const mvUp = button("↑", { size: "sm", title: "Try earlier", disabled: idx === 0 });
          const mvDown = button("↓", { size: "sm", title: "Try later", disabled: idx === targets.length - 1 });
          const rem = button("", { size: "sm", iconName: "x", title: "Remove", disabled: targets.length <= 1 });
          mvUp.addEventListener("click", () => { [targets[idx - 1], targets[idx]] = [targets[idx], targets[idx - 1]]; rebuildTargets(); });
          mvDown.addEventListener("click", () => { [targets[idx + 1], targets[idx]] = [targets[idx], targets[idx + 1]]; rebuildTargets(); });
          rem.addEventListener("click", () => { targets.splice(idx, 1); rebuildTargets(); });
          targetsHost.append(h("div", { class: "route-target" },
            chain ? h("span", { class: "route-target-n", text: `${idx + 1}.` }) : null,
            picker,
            chain ? h("div", { class: "row" }, mvUp, mvDown, rem) : null));
        });
        if (chain && targets.length < 8) {
          targetsHost.append(button("Add a fallback", { size: "sm", iconName: "plus", onClick: () => { targets.push(""); rebuildTargets(); } }));
        }
      };
      kind.addEventListener("change", rebuildTargets);
      rebuildTargets();

      const save = button(isEdit ? "Save changes" : "Save route", { variant: "primary" });
      save.addEventListener("click", () => busy(save, async () => {
        const normalized = targets.map(t => t.trim()).filter(Boolean);
        try {
          const body = { name: name.value.trim(), kind: kind.value, targets: normalized, enabled: enabled.input.checked };
          if (isEdit) await api.put(`router/routes/${encodeURIComponent(existing.id)}`, body);
          else await api.post("router/routes", body);
          toast(isEdit ? "Route saved." : `Route added. Pick "${body.name}" in your agent's model menu.`);
          routeEditId = null;
          await reload();
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      const remove = isEdit ? button("Delete", { variant: "danger", iconName: "trash" }) : null;
      remove?.addEventListener("click", async () => {
        const ok = await confirmDialog({ title: `Delete ${existing.name}?`, message: "Agents can no longer ask for this name.", confirmLabel: "Delete route", danger: true });
        if (!ok) return;
        await busy(remove, async () => {
          try {
            await api.del(`router/routes/${encodeURIComponent(existing.id)}`);
            toast("Route deleted.");
            routeEditId = null;
            await reload();
          } catch (error) {
            status.replaceChildren(notice(error.message, "danger"));
          }
        });
      });
      const cancel = isEdit ? button("Cancel", { size: "sm", onClick: () => { routeEditId = null; drawRoutes(); } }) : null;

      return card({
        title: isEdit ? existing.name : "New route",
        body: [
          h("div", { class: "grid-2" },
            field("Name", name, { required: true, help: "What you pick in the agent's menu. Lower-case letters, digits, dot, underscore, hyphen." }),
            field("Type", kind)),
          h("div", { class: "field" },
            h("span", { class: "field-label", text: "Model" + (kind.value === "combo" ? "s, in the order they are tried" : "") }),
            targetsHost),
          enabled,
          status,
        ],
        footer: [remove, cancel, h("span", { class: "grow" }), save],
      });
    }

    // ---- smart routing ---------------------------------------------------------------

    function drawSmart() {
      const sw = toggle("Smart routing", !!state.smart_routing);
      const status = h("div", { class: "stack" });
      sw.input.addEventListener("change", async () => {
        const enabled = sw.input.checked;
        try {
          await api.put("router/smart", { enabled });
          state.smart_routing = enabled;
          toast(enabled ? "Smart routing is on." : "Smart routing is off.");
          clear(status);
        } catch (error) {
          sw.input.checked = !enabled;
          status.replaceChildren(notice(error.message, "danger"));
        }
      });

      const groups = state.smart_groups || [];
      const groupList = groups.length
        ? h("div", { class: "smart-groups" }, groups.map(g => h("div", { class: "smart-group" },
            h("div", { class: "smart-group-model mono small", text: g.model }),
            h("div", { class: "smart-group-chain mono", text: g.targets.join(" → ") }))))
        : h("p", { class: "muted small", text: "No model is served by more than one provider yet, so there is nothing to switch between. Add the same model from another provider — a second Codex account is added for you — and it appears here." });

      mount(smartHost,
        card({
          title: "Smart routing",
          description: "When a model fails — a usage limit, an outage, a refused key — use the same model from another provider instead.",
          body: [
            sw,
            h("p", { class: "muted small", text: "The provider you picked is tried first. After it, the same model elsewhere: your plans first (each ChatGPT account, then OpenCode Go), then models on this computer, and pay-per-token APIs last because they cost more with every request. A model name several providers list starts with the cheapest. Your routes still apply; smart routing only adds the fallbacks after them." }),
            h("details", { class: "meta-disclosure", open: groups.length > 0 && groups.length <= 6 },
              h("summary", { text: `Models it can switch (${groups.length})` }),
              groupList),
            status,
          ],
        }),
      );
    }

    // ---- default route ---------------------------------------------------------------

    function drawDefault() {
      const sel = h("select", { class: "select" }, h("option", { value: "", text: "None — refuse the request" }));
      const current = state.default_route || "";
      if (state.routes.length) {
        const og = h("optgroup", { label: "Routes" });
        for (const r of state.routes) og.append(h("option", { value: r.name, text: r.name, selected: r.name === current }));
        sel.append(og);
      }
      for (const group of modelChoices()) {
        const og = h("optgroup", { label: group.label });
        for (const option of group.options) og.append(h("option", { value: option, text: option, selected: option === current }));
        sel.append(og);
      }
      const status = h("div", { class: "stack" });
      const save = button("Save", { variant: "primary", size: "sm" });
      save.addEventListener("click", () => busy(save, async () => {
        try {
          await api.put("router/default", { route: sel.value || null });
          state.default_route = sel.value || null;
          toast(sel.value ? `Unknown models now go to ${sel.value}.` : "Default cleared.");
          clear(status);
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      mount(defaultHost,
        card({
          title: "When an agent asks for a model the router doesn't know",
          description: "A model name no provider lists and no route names. Optional. Claude Code's own claude-… models don't land here: they go to Anthropic with Claude Code's own sign-in.",
          body: [h("div", { class: "row route-default" }, sel, save), status],
        }),
      );
    }

    // ---- recent requests and summary -------------------------------------------------

    async function loadLedger() {
      try {
        const res = await api.get(`router/requests?limit=50`);
        requests = res.requests || [];
      } catch (error) {
        requests = [];
        ledgerHost.replaceChildren(notice(error.message, "danger"));
        return;
      }
      drawLedger();
    }

    async function loadSummary() {
      try {
        const res = await api.get(`router/summary?days=${encodeURIComponent(String(summaryDays))}`);
        summary = res.summary || [];
      } catch (error) {
        summary = [];
        summaryHost.replaceChildren(notice(error.message, "danger"));
        return;
      }
      drawSummary();
    }

    function drawLedger() {
      if (!requests.length) {
        mount(ledgerHost,
          card({
            title: "Recent requests",
            description: "Proxy-observed numbers. These are NOT the same records as the Usage page and must not be added to them.",
            body: empty("No requests yet", "Send a request through the router to see it here.", "route"),
          }),
        );
        return;
      }
      const rows = requests.map(entry => {
        const req = entry.request || {};
        const atts = entry.attempts || [];
        const final = req.upstream_slug && req.model ? `${req.upstream_slug}/${req.model}` : (req.upstream_slug || req.model || "—");
        const tokens = [
          req.input_tokens != null ? `in ${req.input_tokens}` : null,
          req.output_tokens != null ? `out ${req.output_tokens}` : null,
          req.cached_input_tokens != null ? `cached ${req.cached_input_tokens}` : null,
          req.reasoning_tokens != null ? `reasoning ${req.reasoning_tokens}` : null,
        ].filter(Boolean).join(" · ") || "—";
        const attemptRows = atts.map(a => h("tr", null,
          h("td", { text: String(a.ordinal ?? "") }),
          h("td", { text: `${a.upstream_slug || ""}/${a.model || ""}` }),
          h("td", { text: a.status != null ? String(a.status) : "—" }),
          h("td", { text: a.error_code || "—" }),
          h("td", { text: a.duration_ms != null ? `${a.duration_ms} ms` : "—" }),
        ));

        return h("details", { class: "card" },
          h("summary", { class: "row-between", style: { padding: "10px 14px", cursor: "pointer" } },
            h("div", null,
              h("div", { class: "small", text: `${fmtTime(req.started_at)} · ${req.requested_model || "—"} · ${req.route_kind || "—"}${req.route_name ? `/${req.route_name}` : ""} → ${final} ${req.stream ? "· stream" : ""}` }),
              h("div", { class: "muted small", text: `status ${req.status ?? "—"} · ${tokens}${req.error_code ? ` · ${req.error_code}` : ""}` }),
            ),
            h("div", { class: "row" }, outcomeBadge(req.outcome), req.status != null ? badge(String(req.status), req.status >= 200 && req.status < 400 ? "ok" : "danger") : null),
          ),
          h("div", { class: "card-body" },
            h("div", { class: "stack" },
              h("div", { class: "kv" },
                h("span", { class: "muted small", text: "Requested model: " }), h("span", { class: "mono small", text: req.requested_model || "—" }),
                h("span", { class: "muted small", text: "Route: " }), h("span", { class: "small", text: `${req.route_kind || "—"}${req.route_name ? `/${req.route_name}` : ""}` }),
                h("span", { class: "muted small", text: "Final: " }), h("span", { class: "mono small", text: final }),
                h("span", { class: "muted small", text: "Inbound wire: " }), h("span", { class: "small", text: req.inbound_wire || "—" }),
                h("span", { class: "muted small", text: "Stream: " }), h("span", { class: "small", text: req.stream ? "yes" : "no" }),
                h("span", { class: "muted small", text: "Outcome: " }), h("span", { class: "small", text: req.outcome || "—" }),
              ),
              atts.length ? h("div", { class: "table-wrap" },
                h("table", { class: "table" },
                  h("thead", null, h("tr", null, h("th", { text: "#" }), h("th", { text: "Upstream / model" }), h("th", { text: "Status" }), h("th", { text: "Error" }), h("th", { text: "Duration" }))),
                  h("tbody", null, attemptRows))) : h("p", { class: "muted small", text: "No attempts recorded." }),
            ),
          ),
        );
      });

      const refreshBtn = button("Refresh", { size: "sm", iconName: "refresh", onClick: loadLedger });
      mount(ledgerHost,
        card({
          title: "Recent requests",
          description: "Proxy-observed numbers. These are NOT the same records as the Usage page and must not be added to them.",
          actions: refreshBtn,
          body: [
            h("p", { class: "muted small", text: "Token counts are reported by the upstream. They are proxy-observed and separate from the Usage page." }),
            h("div", { class: "stack" }, rows),
          ],
        }),
      );
    }

    function drawSummary() {
      const periodSel = select([["1", "Last day"], ["7", "Last 7 days"], ["30", "Last 30 days"]], String(summaryDays));
      periodSel.addEventListener("change", () => { summaryDays = Number(periodSel.value); loadSummary(); });

      let body;
      if (!summary.length) {
        body = empty("No totals yet", "Totals appear after requests go through the router.", "pulse");
      } else {
        const rows = summary.map(s => h("tr", null,
          h("td", { text: `${s.upstream_slug}/${s.model}` }),
          h("td", { text: String(s.count) }),
          h("td", { text: String(s.ok_count) }),
          h("td", { text: String(s.input_tokens_sum) }),
          h("td", { text: String(s.cached_input_tokens_sum) }),
          h("td", { text: String(s.output_tokens_sum) }),
          h("td", { text: String(s.reasoning_tokens_sum) }),
        ));
        body = h("div", { class: "table-wrap" },
          h("table", { class: "table" },
            h("thead", null, h("tr", null,
              h("th", { text: "Upstream / model" }), h("th", { text: "Requests" }), h("th", { text: "OK" }),
              h("th", { text: "Input" }), h("th", { text: "Cached" }), h("th", { text: "Output" }), h("th", { text: "Reasoning" }))),
            h("tbody", null, rows)));
      }

      mount(summaryHost,
        card({
          title: "Totals by model",
          description: "Proxy-observed totals for the selected period. Do not add these to the Usage page.",
          actions: periodSel,
          body,
        }),
      );
    }

    // ---- agents ----------------------------------------------------------------------

    let agentsData = null;

    const SLOT_LABELS = {
      default: "Default model",
      opus: "Opus menu entry",
      sonnet: "Sonnet menu entry",
      haiku: "Haiku menu entry",
      small_fast: "Background / small-fast model",
    };

    async function drawAgents() {
      try {
        agentsData = await api.get("router/agents");
      } catch (error) {
        mount(agentsHost, notice(error.message, "danger"));
        return;
      }

      const selectable = agentsData.selectable || [];
      const cards = agentsData.agents.map(agent => agentCard(agent, selectable));
      mount(agentsHost,
        card({
          title: "Agents on this computer",
          description: "Every Codex and Claude Code account on this computer — the same list as Live quota. Connecting writes that account's own configuration so its model picker lists your providers' models. "
            + "A copy of the file is kept first, and disconnecting puts your settings back.",
          body: h("div", { class: "stack" }, cards),
        }),
      );
    }

    function modelSelect(value, selectable, { allowEmpty = false, emptyLabel = "Leave unchanged" } = {}) {
      const options = selectable.map(model => [model, model]);
      if (allowEmpty) options.unshift(["", emptyLabel]);
      return select(options, value || (allowEmpty ? "" : selectable[0]));
    }

    function agentCard(agent, selectable) {
      const head = h("div", { class: "quota-account-head" },
        h("div", null,
          h("span", { class: "eyebrow", text: agent.kind === "codex" ? "Codex" : "Claude Code" }),
          h("h2", { class: "quota-account-name", text: agent.account_label || agent.display_name })),
        agent.connected ? badge("Connected", "ok") : badge(agent.detected ? "Not connected" : "Not installed", agent.detected ? "warn" : "danger"));

      const lines = [h("p", { class: "muted small", text: agent.config_path })];
      if (agent.kind === "codex") {
        const plan = state.upstreams.find(u => u.auth === "codex_chatgpt" && u.enabled);
        lines.push(notice(plan
          ? `Codex's /model menu lists exactly the models below; it has no "add to my own list" mode like Claude Code's. Your ChatGPT plan models (${plan.slug}/…) are among them and keep Codex's own prompt and settings.`
          : `Codex's /model menu lists exactly the models below; it has no "add to my own list" mode like Claude Code's. To keep GPT models in it, add the ChatGPT plan provider — they then appear here and behave as Codex's own.`, "info"));
      }
      if (agent.blocked) lines.push(notice(agent.blocked, "warn"));
      if (agent.connected && agent.selected_model)
        lines.push(h("p", { class: "small", text: `Sends ${agent.selected_model} by default.` }));
      if (agent.catalog_path && agent.catalog_model_count > 0)
        lines.push(h("p", { class: "muted small", text: `${agent.catalog_model_count} models offered through ${agent.catalog_path}` }));

      if (!selectable.length || !agent.detected) {
        return h("section", { class: "card router-agent" }, head, h("div", { class: "stack" }, lines));
      }

      // The form doubles as the reconnect form, so it starts from whatever is configured now.
      const controls = [];
      const mainSelect = modelSelect(agent.selected_model, selectable);
      controls.push(field(agent.kind === "claude_code" ? SLOT_LABELS.default : "Model", mainSelect,
        { help: "What this agent asks for unless you pick something else in its own menu." }));

      const slotSelects = {};
      for (const slot of (agentsData.slots || [])) {
        if (agent.kind !== "claude_code" || slot === "default") continue;
        const current = (agent.model_slots || {})[slot] || "";
        const control = modelSelect(current, selectable, { allowEmpty: true, emptyLabel: "Leave this entry alone" });
        slotSelects[slot] = control;
        controls.push(field(SLOT_LABELS[slot] || slot, control,
          { help: "Picking this entry in Claude Code's own model menu sends this selector." }));
      }

      const optionControls = {};
      for (const option of (agent.options || [])) {
        const current = (agent.option_values || {})[option.id] || "";
        // An on/off setting is a switch, off unless it was turned on.
        if (option.choices?.length === 2 && option.choices.includes("on") && option.choices.includes("off")) {
          const sw = toggle(option.display_name, current === "on", { help: option.description });
          optionControls[option.id] = { get value() { return sw.input.checked ? "on" : "off"; } };
          controls.push(sw);
          continue;
        }
        const control = option.is_model_selector
          ? modelSelect(current, selectable, { allowEmpty: true, emptyLabel: "Leave unchanged" })
          : select([["", "Leave unchanged"], ...option.choices.map(c => [c, c])], current);
        optionControls[option.id] = control;
        controls.push(field(option.display_name, control, { help: option.description }));
      }

      const connect = button(agent.connected ? "Apply" : "Connect", { variant: "primary", iconName: "check" });
      connect.addEventListener("click", () => busy(connect, async () => {
        const body = { model: mainSelect.value, model_slots: {}, options: {} };
        for (const [slot, control] of Object.entries(slotSelects))
          if (control.value) body.model_slots[slot] = control.value;
        for (const [id, control] of Object.entries(optionControls))
          if (control.value) body.options[id] = control.value;
        try {
          const result = await api.post(`router/agents/${encodeURIComponent(agent.id)}/connect`, body);
          toast(result.message || "Connected.");
          await drawAgents();
        } catch (error) {
          toast(error.message, "error");
        }
      }));

      const actions = [connect];
      if (agent.connected) {
        const disconnect = button("Disconnect", { iconName: "x" });
        disconnect.addEventListener("click", () => busy(disconnect, async () => {
          const ok = await confirmDialog({
            title: `Disconnect ${agent.display_name}?`,
            message: "Its own model settings are put back, and it stops routing through AgentNotify.",
            confirmLabel: "Disconnect",
          });
          if (!ok) return;
          try {
            const result = await api.post(`router/agents/${encodeURIComponent(agent.id)}/disconnect`, {});
            toast(result.message || "Disconnected.");
            await drawAgents();
          } catch (error) {
            toast(error.message, "error");
          }
        }));
        actions.push(disconnect);
      }

      const backups = (agent.backups || []).map(backup => {
        const restore = button("Restore", { size: "sm", iconName: "refresh" });
        restore.addEventListener("click", () => busy(restore, async () => {
          const ok = await confirmDialog({
            title: "Restore this copy?",
            message: `${agent.config_path} is replaced with the copy taken ${fmtTime(backup.created_at)}. `
              + "The file as it is now is itself copied first, so this can be stepped back.",
            confirmLabel: "Restore",
            danger: true,
          });
          if (!ok) return;
          try {
            const result = await api.post(`router/agents/${encodeURIComponent(agent.id)}/restore`, { backup_id: backup.id });
            toast(result.message || "Restored.");
            await drawAgents();
          } catch (error) {
            toast(error.message, "error");
          }
        }));
        return h("div", { class: "balance-row" },
          h("div", { class: "balance-head" },
            h("span", { class: "small", text: `${fmtTime(backup.created_at)} · ${backup.reason}` }),
            restore));
      });

      const backupBlock = backups.length
        ? h("details", { class: "meta-disclosure" },
            h("summary", { text: `Saved copies of this file (${backups.length})` }),
            h("div", { class: "stack" },
              h("p", { class: "muted small", text: "AgentNotify copies the file before every change it makes. Restoring writes one of those copies back." }),
              backups))
        : null;

      return h("section", { class: "card router-agent" }, head,
        h("div", { class: "stack" }, lines, controls, h("div", { class: "row" }, actions), backupBlock));
    }

    const sections = {
      providers: [statusHost, upstreamsHost],
      routing: [smartHost, routesHost, defaultHost],
      agents: [agentsHost, connectHost],
      activity: [ledgerHost, summaryHost],
    };

    mount(page,
      pageHead(TITLES[section][0], TITLES[section][1], null),
      state.enabled ? null : offBanner(),
      sections[section],
      section === "activity"
        ? h("p", { class: "muted small page-footnote", text: "Router ledger tokens are proxy-observed. They are separate from the Usage page and must not be added together." })
        : null,
    );

    if (section === "providers") { drawStatus(); drawUpstreams(); }
    if (section === "routing") { drawSmart(); drawRoutes(); drawDefault(); }
    if (section === "agents") { drawConnect(); await drawAgents(); }
    if (section === "activity") {
      drawLedger();
      drawSummary();
      await loadLedger();
      await loadSummary();
    }
}
