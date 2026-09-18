import { api } from "../api.js";
import {
  h, mount, clear, pageHead, badge, button, busy, empty, toast, card, notice,
  field, input, select, checkbox, toggle, confirmDialog,
} from "../dom.js";

// Page titles live here so every section shares one voice.
const TITLES = {
  providers: ["Providers", "The upstream providers this machine can route model requests to."],
  routing: ["Routing", "Which model selector goes where, and what happens when a target fails."],
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
    let upstreamEditId = null;
    let routeEditId = null;
    let requests = [];
    let summary = [];
    let summaryDays = 1;

    const agentsHost = h("div");
    const statusHost = h("div");
    const connectHost = h("div");
    const upstreamsHost = h("div");
    const routesHost = h("div");
    const defaultHost = h("div");
    const ledgerHost = h("div");
    const summaryHost = h("div");

    const reload = async () => {
      try {
        state = await api.get("router");
        drawStatus();
        drawConnect();
        drawUpstreams();
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
      const enabledToggle = toggle("Router enabled", state.enabled);
      const baseLine = copyBlock(state.base_url || "");
      const anthLine = copyBlock(state.anthropic_base_url || "");
      const offNotice = state.enabled ? null : notice("The router is off. No request is sent to any model provider until you turn it on. The rest of this page stays visible so you can prepare upstreams and routes first.", "info");

      const regen = button("Regenerate key", { iconName: "refresh" });
      regen.addEventListener("click", () => busy(regen, async () => {
        const ok = await confirmDialog({
          title: "Regenerate router key?",
          message: "Every agent configured with this key stops working until you update its configuration. This cannot be undone.",
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
          if (result.key) showKeyOnce(result.key);
          toast(desired ? "Router enabled." : "Router disabled.");
          await reload();
          // preserve key reveal if it was just shown (reload clears hosts but keyRevealHost content is recreated)
          // Re-show if this call generated a key and we already showed it: need to keep it.
          // Draw again will clear keyRevealHost, so re-add after reload if needed.
          if (result.key) showKeyOnce(result.key);
        } catch (error) {
          enabledToggle.input.checked = !desired;
          toast(error.message, "error");
        }
      }));

      mount(statusHost,
        card({
          title: "Status",
          description: "Off by default. The router handles only traffic that reaches its own /router/v1 routes.",
          body: [
            h("div", { class: "fields" },
              h("div", null, enabledToggle),
              keyRevealHost,
              offNotice,
              h("div", { class: "stack" },
                field("Responses and Chat base URL", baseLine),
                field("Anthropic base URL", anthLine),
                h("p", { class: "muted small", text: state.has_key ? "A router key is stored. Regenerating replaces it." : "No router key yet. Enable the router to generate one." }),
                regen,
              ),
            ),
          ],
        }),
      );
    }

    // ---- connect an agent ------------------------------------------------------------

    function drawConnect() {
      const p = state.base_url || "http://127.0.0.1:PORT/router/v1";
      const anthBase = state.anthropic_base_url || "http://127.0.0.1:PORT/router";
      // Derive port from base_url if possible for substitution realism; otherwise keep as is.
      const codexBlock = `model_provider = "agentnotify"\nmodel = "combo/coding"\n\n[model_providers.agentnotify]\nname = "AgentNotify router"\nbase_url = "${p}"\nenv_key = "AGENTNOTIFY_ROUTER_KEY"\nwire_api = "responses"`;
      const claudeBlock = `export ANTHROPIC_BASE_URL=${anthBase}\nexport ANTHROPIC_AUTH_TOKEN="$(agentnotify router key)"\nexport ANTHROPIC_MODEL=combo/coding`;

      mount(connectHost,
        card({
          title: "Connect an agent",
          description: "Point the agent at the broker. AgentNotify does not edit another agent's configuration files.",
          body: [
            h("div", { class: "stack" },
              h("h3", { class: "card-title", text: "Codex (~/.codex/config.toml)" }),
              copyBlock(codexBlock),
              h("h3", { class: "card-title", text: "Claude Code (environment)" }),
              copyBlock(claudeBlock),
              h("p", { class: "muted small", text: "Set AGENTNOTIFY_ROUTER_KEY to the one-time key shown above. Restart the agent after changing configuration." }),
            ),
          ],
        }),
      );
    }

    // ---- upstreams -------------------------------------------------------------------

    function upstreamCard(u) {
      const isSelected = u.id === upstreamEditId;
      return h("button", {
        type: "button",
        class: "list-item",
        "aria-selected": String(isSelected),
        onClick: () => { upstreamEditId = u.id; drawUpstreams(); },
      },
        h("div", { class: "list-main" },
          h("div", { class: "list-title", text: u.label }),
          h("div", { class: "list-sub", text: `${u.slug} · ${u.wire} · ${u.base_url} · ${u.models?.length || 0} models` })),
        h("div", { class: "row" },
          u.enabled ? badge("On", "ok") : badge("Off"),
          u.has_key ? badge("Key", "info") : badge("No key")),
      );
    }

    function drawUpstreams() {
      const listBody = h("div", { class: "list" });
      if (state.upstreams.length === 0) {
        listBody.append(empty("No upstreams yet", "Add one below. Pick a preset to fill the wire and base URL, then set its slug, label, and models.", "route"));
      } else {
        for (const u of state.upstreams) listBody.append(upstreamCard(u));
      }

      const editorHost = h("div");
      if (state.upstreams.length === 0) {
        editorHost.append(notice("Add your first upstream. Its slug becomes the provider part of provider/model, for example openai/gpt-4o. Declare the native model IDs you want to expose.", "info"));
      }

      const editing = upstreamEditId ? state.upstreams.find(x => x.id === upstreamEditId) : null;
      editorHost.append(buildUpstreamForm(editing));

      const addBtn = button(upstreamEditId ? "Add upstream" : "Add upstream", { variant: upstreamEditId ? null : "primary", size: "sm", iconName: "plus", onClick: () => { upstreamEditId = null; drawUpstreams(); } });
      const showEmpty = state.upstreams.length === 0;

      mount(upstreamsHost,
        h("div", { class: "section-heading" },
          h("div", null, h("h2", { text: "Upstreams" }), h("p", { class: "muted small", text: "Providers the router can forward to. Keys are write-only." })),
          addBtn),
        showEmpty ? empty("No upstreams configured", "Use the form below to add one.", "route") : null,
        h("div", { class: "split" },
          h("div", { class: "sticky" }, card({ title: "Configured", description: "Select one to edit it.", body: listBody })),
          editorHost),
      );
    }

    function buildUpstreamForm(existing) {
      const isEdit = !!existing;
      const presetChoices = [["", "Custom"], ...state.presets.map(p => [p.id, `${p.display_name} · ${p.base_url}`])];
      const preset = select(presetChoices, "");
      const slug = input({ value: existing?.slug || "", maxlength: 32, placeholder: "openai", autocomplete: "off", spellcheck: "false" });
      const label = input({ value: existing?.label || "", maxlength: 60, placeholder: "OpenAI", autocomplete: "off" });
      const wire = select(wireOptions, existing?.wire || "openai_chat");
      const baseUrl = input({ value: existing?.base_url || "", placeholder: "https://api.openai.com/v1", autocomplete: "off", spellcheck: "false" });
      const apiKey = input({ type: "password", autocomplete: "off", placeholder: isEdit ? "Leave blank to keep the stored key" : "Paste the provider key", spellcheck: "false" });
      const ack = checkbox("I understand this key is stored encrypted for this user and is sent only to the host above.", false);
      const clearKey = isEdit && existing.has_key ? checkbox("Remove the stored key", false) : null;
      if (clearKey) {
        clearKey.input.addEventListener("change", () => {
          if (clearKey.input.checked) apiKey.value = "";
          apiKey.disabled = clearKey.input.checked;
        });
      }
      const models = h("textarea", {
        class: "textarea",
        rows: 5,
        placeholder: "One model per line, for example:\ngpt-4o\ngpt-4o-mini",
        value: (existing?.models || []).join("\n"),
      });
      const enabled = toggle("Enabled", existing ? existing.enabled : true);
      const status = h("div", { class: "stack" });

      preset.addEventListener("change", () => {
        const p = state.presets.find(x => x.id === preset.value);
        if (!p) return;
        wire.value = p.wire;
        baseUrl.value = p.base_url;
      });

      const save = button(isEdit ? "Save changes" : "Save upstream", { variant: "primary" });
      save.addEventListener("click", () => busy(save, async () => {
        const modelList = models.value.split("\n").map(s => s.trim()).filter(Boolean);
        const body = {
          slug: slug.value.trim(),
          label: label.value.trim(),
          wire: wire.value,
          base_url: baseUrl.value.trim(),
          api_key: apiKey.value || null,
          models: modelList,
          enabled: enabled.input.checked,
          clear_key: clearKey?.input.checked || false,
        };
        if (apiKey.value) body.ack_key_storage = ack.input.checked;
        // Endpoint also accepts acknowledge_risk; ack_key_storage is preferred.
        if (apiKey.value && !ack.input.checked) {
          status.replaceChildren(notice("Tick the acknowledgement to store a key.", "danger"));
          return;
        }
        try {
          if (isEdit) await api.put(`router/upstreams/${encodeURIComponent(existing.id)}`, body);
          else await api.post("router/upstreams", body);
          toast(isEdit ? "Upstream saved." : "Upstream added.");
          upstreamEditId = null;
          await reload();
          clear(status);
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      const remove = isEdit ? button("Delete", { variant: "danger", iconName: "trash" }) : null;
      remove?.addEventListener("click", async () => {
        const ok = await confirmDialog({ title: `Delete ${existing.label}?`, message: "The router can no longer send requests to or through this upstream.", confirmLabel: "Delete upstream", danger: true });
        if (!ok) return;
        await busy(remove, async () => {
          try {
            await api.del(`router/upstreams/${encodeURIComponent(existing.id)}`);
            toast("Upstream deleted.");
            upstreamEditId = null;
            await reload();
          } catch (error) {
            status.replaceChildren(notice(error.message, "danger"));
          }
        });
      });

      const cancel = isEdit ? button("Cancel", { size: "sm", onClick: () => { upstreamEditId = null; drawUpstreams(); } }) : null;

      return card({
        title: isEdit ? existing.label : "New upstream",
        description: isEdit ? `${existing.slug} · ${existing.wire}` : "Choose a preset or enter the details directly.",
        body: [
          field("Preset", preset, { help: "Fills wire and base URL. You can still edit them." }),
          h("div", { class: "grid-2" }, field("Slug", slug, { required: true, help: "Lower-case letters, digits, hyphens. This is the provider part of provider/model." }), field("Label", label, { required: true })),
          h("div", { class: "grid-2" }, field("Wire", wire, { required: true }), field("Base URL", baseUrl, { required: true, help: "Must be https, or http only for loopback." })),
          field("API key", apiKey, { help: isEdit ? "Leave blank to keep the stored key. Stored encrypted and never shown." : "Stored encrypted and never shown." }),
          ack,
          clearKey || null,
          field("Models", models, { help: "One native model ID per line. These are the model names after the slash in provider/model." }),
          h("div", { class: "field" }, h("span", { class: "field-label", text: "Status" }), enabled),
          status,
        ],
        footer: [remove, cancel, h("span", { class: "grow" }), save],
      });
    }

    // ---- aliases and combos ----------------------------------------------------------

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
          h("div", { class: "list-sub", text: `${r.kind} · ${(r.targets || []).join(" → ")}` })),
        r.enabled ? badge("On", "ok") : badge("Off"),
      );
    }

    function drawRoutes() {
      const listBody = h("div", { class: "list" });
      if (!state.routes.length) {
        listBody.append(empty("No routes yet", "An alias maps one provider/model to a single target. A combo tries its targets in order until one succeeds.", "route"));
      } else {
        for (const r of state.routes) listBody.append(routeCard(r));
      }

      const editing = routeEditId ? state.routes.find(x => x.id === routeEditId) : null;
      const editorHost = h("div", null, buildRouteForm(editing));
      const addBtn = button("Add route", { variant: routeEditId ? null : "primary", size: "sm", iconName: "plus", onClick: () => { routeEditId = null; drawRoutes(); } });

      mount(routesHost,
        h("div", { class: "section-heading" },
          h("div", null, h("h2", { text: "Aliases and combos" }), h("p", { class: "muted small", text: "A combo tries its targets in order (failover order)." })),
          addBtn),
        h("div", { class: "split" },
          h("div", { class: "sticky" }, card({ title: "Routes", body: listBody })),
          editorHost),
      );
    }

    function buildRouteForm(existing) {
      const isEdit = !!existing;
      const name = input({ value: existing?.name || "", maxlength: 64, placeholder: "coding", autocomplete: "off", spellcheck: "false" });
      const kind = select([["alias", "alias — one target"], ["combo", "combo — ordered failover"]], existing?.kind || "alias");
      const enabled = toggle("Enabled", existing ? existing.enabled : true);
      const status = h("div", { class: "stack" });

      // Targets handling
      let targets = (existing?.targets || []).slice();
      if (!targets.length) targets = [""];

      const targetsHost = h("div", { class: "stack" });

      const upstreamOptions = state.upstreams.map(u => [u.slug, `${u.label} (${u.slug})`]);

      const rebuildTargets = () => {
        clear(targetsHost);
        targets.forEach((t, idx) => {
          const slash = t.indexOf("/");
          const curSlug = slash >= 0 ? t.slice(0, slash) : (upstreamOptions[0]?.[0] || "");
          const curModel = slash >= 0 ? t.slice(slash + 1) : t;
          const slugSel = select(upstreamOptions.length ? upstreamOptions : [["", "No upstreams — add one first"]], curSlug);
          const modelInput = input({ value: curModel, placeholder: "model id", autocomplete: "off", spellcheck: "false" });
          const sync = () => { targets[idx] = slugSel.value ? `${slugSel.value}/${modelInput.value.trim()}` : modelInput.value.trim(); };
          slugSel.addEventListener("change", sync);
          modelInput.addEventListener("input", sync);

          const upBtn = button("", { size: "sm", iconName: "plus", title: "Move up" });
          // reuse plus rotated? We'll make simple text buttons for move up/down
          const mvUp = button("↑", { size: "sm", title: "Move up", disabled: idx === 0 });
          const mvDown = button("↓", { size: "sm", title: "Move down", disabled: idx === targets.length - 1 });
          const rem = button("", { size: "sm", iconName: "x", title: "Remove" });
          // Use handler that respects alias length
          mvUp.addEventListener("click", () => {
            const tmp = targets[idx - 1]; targets[idx - 1] = targets[idx]; targets[idx] = tmp; rebuildTargets();
          });
          mvDown.addEventListener("click", () => {
            const tmp = targets[idx + 1]; targets[idx + 1] = targets[idx]; targets[idx] = tmp; rebuildTargets();
          });
          rem.addEventListener("click", () => {
            if (kind.value === "alias" && targets.length <= 1) {
              toast("Alias must have exactly one target.", "error");
              return;
            }
            targets.splice(idx, 1);
            if (!targets.length) targets = [""];
            rebuildTargets();
          });

          const row = h("div", { class: "row" },
            field(`Target ${idx + 1}`, slugSel),
            field("Model", modelInput),
            mvUp, mvDown, rem,
          );
          // Show order hint for combos
          targetsHost.append(row);
        });
        const addTarget = button("Add target", { size: "sm", iconName: "plus" });
        addTarget.addEventListener("click", () => {
          if (kind.value === "alias" && targets.length >= 1) {
            toast("Alias can have only one target.", "error");
            return;
          }
          if (targets.length >= 8) {
            toast("A combo can have at most 8 targets.", "error");
            return;
          }
          targets.push("");
          rebuildTargets();
        });
        // Control add button visibility
        if (kind.value === "combo" || targets.length === 0) targetsHost.append(addTarget);
        // Sync alias constraint: hide extra controls if needed
        if (kind.value === "alias" && targets.length > 1) {
          // Trim to one
          targets = [targets[0]];
          rebuildTargets();
          return;
        }
      };
      kind.addEventListener("change", () => rebuildTargets());
      rebuildTargets();

      const save = button(isEdit ? "Save changes" : "Save route", { variant: "primary" });
      save.addEventListener("click", () => busy(save, async () => {
        // Collect current text values (in case edits not synced due to stale closures, re-read)
        // targets array is kept in sync via events; final trim
        const normalized = targets.map(t => t.trim()).filter(Boolean);
        try {
          const body = { name: name.value.trim(), kind: kind.value, targets: normalized, enabled: enabled.input.checked };
          if (isEdit) await api.put(`router/routes/${encodeURIComponent(existing.id)}`, body);
          else await api.post("router/routes", body);
          toast(isEdit ? "Route saved." : "Route added.");
          routeEditId = null;
          await reload();
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      const remove = isEdit ? button("Delete", { variant: "danger", iconName: "trash" }) : null;
      remove?.addEventListener("click", async () => {
        const ok = await confirmDialog({ title: `Delete ${existing.name}?`, message: "Requests can no longer use this route name.", confirmLabel: "Delete route", danger: true });
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
        description: kind.value === "combo" ? "A combo tries its targets in order." : "An alias has exactly one target.",
        body: [
          h("div", { class: "grid-2" }, field("Name", name, { required: true, help: "Lower-case, digits, dot, underscore, hyphen." }), h("div", { class: "field" }, h("span", { class: "field-label", text: "Status" }), enabled)),
          field("Kind", kind, { required: true }),
          h("div", null,
            h("span", { class: "field-label", text: "Targets (order is failover order)" }),
            h("p", { class: "muted small", text: "Pick upstream and type the native model. Order matters for combos." }),
            targetsHost),
          status,
        ],
        footer: [remove, cancel, h("span", { class: "grow" }), save],
      });
    }

    // ---- default route ---------------------------------------------------------------

    function drawDefault() {
      const options = [["", "none"]];
      for (const r of state.routes) {
        options.push([r.name, r.name]);
        options.push([`combo/${r.name}`, `combo/${r.name}`]);
      }
      for (const u of state.upstreams) {
        for (const m of (u.models || [])) {
          options.push([`${u.slug}/${m}`, `${u.slug}/${m}`]);
        }
      }
      const current = state.default_route || "";
      const sel = select(options, current);
      const status = h("div", { class: "stack" });
      const save = button("Save default", { variant: "primary", size: "sm" });
      save.addEventListener("click", () => busy(save, async () => {
        try {
          await api.put("router/default", { route: sel.value || null });
          state.default_route = sel.value || null;
          toast(sel.value ? `Default set to ${sel.value}.` : "Default cleared.");
          status.replaceChildren(notice(sel.value ? `Default route is now ${sel.value}.` : "Default route cleared.", "ok"));
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      mount(defaultHost,
        card({
          title: "Default route",
          description: "Used when the requested model does not match any route or upstream.",
          body: [
            field("Default", sel),
            h("div", { class: "row" }, save),
            status,
          ],
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
          description: "Connecting writes that agent's own configuration so its model picker lists these models. "
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
          h("span", { class: "eyebrow", text: agent.id }),
          h("h2", { class: "quota-account-name", text: agent.display_name })),
        agent.connected ? badge("Connected", "ok") : badge(agent.detected ? "Not connected" : "Not installed", agent.detected ? "warn" : "danger"));

      const lines = [h("p", { class: "muted small", text: agent.config_path })];
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
      controls.push(field(agent.id === "claude_code" ? SLOT_LABELS.default : "Model", mainSelect,
        { help: "What this agent asks for unless you pick something else in its own menu." }));

      const slotSelects = {};
      for (const slot of (agentsData.slots || [])) {
        if (agent.id !== "claude_code" || slot === "default") continue;
        const current = (agent.model_slots || {})[slot] || "";
        const control = modelSelect(current, selectable, { allowEmpty: true, emptyLabel: "Leave this entry alone" });
        slotSelects[slot] = control;
        controls.push(field(SLOT_LABELS[slot] || slot, control,
          { help: "Picking this entry in Claude Code's own model menu sends this selector." }));
      }

      const optionControls = {};
      for (const option of (agent.options || [])) {
        const current = (agent.option_values || {})[option.id] || "";
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
          const result = await api.post(`router/agents/${agent.id}/connect`, body);
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
            const result = await api.post(`router/agents/${agent.id}/disconnect`, {});
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
            const result = await api.post(`router/agents/${agent.id}/restore`, { backup_id: backup.id });
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
      routing: [routesHost, defaultHost],
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
    if (section === "routing") { drawRoutes(); drawDefault(); }
    if (section === "agents") { drawConnect(); await drawAgents(); }
    if (section === "activity") {
      drawLedger();
      drawSummary();
      await loadLedger();
      await loadSummary();
    }
}
