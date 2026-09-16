import { api } from "../api.js";
import {
  h, mount, icon, clear, pageHead, badge, button, busy, empty, toast, card, notice, field, input, select,
  checkbox, toggle, confirmDialog, timeAgo,
} from "../dom.js";

export default {
  async render(page, ctx) {
    const [kinds, initialProviders] = await Promise.all([api.get("provider-kinds"), api.get("providers")]);
    const kindById = new Map(kinds.map((k) => [k.kind, k]));
    let providers = initialProviders;
    let selectedId = ctx.params.get("provider");
    let stopPairing = null;

    const listBody = h("div", { class: "list" });
    const editorHost = h("div", { class: "stack" });

    const addButton = button("Add channel", { variant: "primary", iconName: "plus", onClick: () => showPicker() });

    mount(page, 
      pageHead("Channels", "Where notifications can go when you are away from this screen. Every channel is off until you enable it, and credentials are encrypted on this computer.", addButton),
      h("div", { class: "split" },
        h("div", { class: "sticky" }, card({ title: "Configured", description: "Select one to edit it.", body: listBody })),
        editorHost));

    const drawList = () => {
      clear(listBody);
      if (providers.length === 0) {
        listBody.append(empty("No channels yet", "Add one to reach your phone, chat, or inbox.", "send"));
        return;
      }
      for (const p of providers) {
        const kind = kindById.get(p.kind);
        listBody.append(h("button", {
          type: "button",
          class: "list-item",
          "aria-selected": String(p.id === selectedId),
          onClick: () => showEditor(p.kind, p),
        },
        h("div", { class: "list-main" },
          h("div", { class: "list-title", text: p.name }),
          h("div", { class: "list-sub", text: kind ? kind.display_name : p.kind })),
        p.enabled ? badge("On", "ok") : badge("Off")));
      }
    };

    const reload = async (keepId) => {
      providers = await api.get("providers");
      if (keepId !== undefined) selectedId = keepId;
      drawList();
    };

    function resetEditor() {
      if (stopPairing) { stopPairing(); stopPairing = null; }
      clear(editorHost);
    }

    function showPlaceholder() {
      resetEditor();
      selectedId = null;
      drawList();
      editorHost.append(card({ body: empty("Choose a channel", "Select a configured channel on the left, or add a new one.", "send") }));
    }

    function showPicker() {
      resetEditor();
      selectedId = null;
      drawList();
      const groups = new Map();
      for (const k of kinds) {
        if (!groups.has(k.category)) groups.set(k.category, []);
        groups.get(k.category).push(k);
      }
      editorHost.append(card({
        title: "Add a channel",
        description: "Pick the service to deliver to. You can add more than one of each.",
        actions: button("Cancel", { size: "sm", onClick: showPlaceholder }),
        body: h("div", null, [...groups].map(([category, items]) => [
          h("div", { class: "kind-cat", text: category }),
          h("div", { class: "kind-grid" }, items.map((k) => h("button", { type: "button", class: "kind", onClick: () => showEditor(k.kind, null) },
            h("span", { class: "kind-name" }, k.display_name, k.paid ? badge("Paid", "warn") : null),
            h("span", { class: "kind-sum", text: k.summary })))),
        ]))
      }));
    }

    function showEditor(kindId, profile) {
      resetEditor();
      const kind = kindById.get(kindId);
      selectedId = profile ? profile.id : null;
      drawList();
      if (!kind) {
        editorHost.append(notice(`This profile uses a channel type this version does not recognise (${kindId}).`, "warn"));
        return;
      }
      editorHost.append(buildEditor(kind, profile));
    }

    function buildEditor(kind, profile) {
      const stored = new Set(profile ? profile.secret_names : []);
      const values = {};
      for (const f of kind.fields) if (f.type !== "secret") values[f.key] = f.default ?? (f.type === "checkbox" ? "false" : "");
      Object.assign(values, profile ? profile.values : {});

      const nameInput = input({ value: profile ? profile.name : kind.display_name, maxlength: 100, autocomplete: "off" });
      const enabled = toggle("Enabled", profile ? profile.enabled : false, {
        help: profile ? null : "New channels start switched off. Turn this on once the test succeeds.",
      });

      const controls = new Map();
      const wrappers = [];
      const main = h("div", { class: "fields" });
      const advanced = h("details", { class: "advanced" }, h("summary", { text: "Advanced" }));
      const advancedBody = h("div", { class: "fields" });
      advanced.append(advancedBody);

      const refreshVisibility = () => {
        for (const { f, wrap } of wrappers) {
          if (!f.show_when) continue;
          wrap.hidden = !f.show_when.values.includes(String(values[f.show_when.key] ?? ""));
        }
      };

      for (const f of kind.fields) {
        const wrap = buildField(f);
        wrappers.push({ f, wrap });
        (f.advanced ? advancedBody : main).append(wrap);
      }
      refreshVisibility();

      function buildField(f) {
        if (f.type === "checkbox") {
          const box = checkbox(f.label, values[f.key] === "true", { help: f.help, onChange: () => { values[f.key] = String(box.input.checked); refreshVisibility(); } });
          controls.set(f.key, { get: () => String(box.input.checked) });
          return h("div", null, box);
        }
        if (f.type === "select") {
          const el = select((f.options || []).map((o) => [o.value, o.label]), values[f.key]);
          el.addEventListener("change", () => { values[f.key] = el.value; refreshVisibility(); });
          controls.set(f.key, { get: () => el.value });
          return field(f.label, el, { help: f.help, required: f.required });
        }
        if (f.type === "secret") {
          const has = stored.has(f.key);
          const el = input({
            type: "password",
            autocomplete: "new-password",
            spellcheck: "false",
            placeholder: has ? "Stored. Leave blank to keep it." : (f.placeholder || ""),
          });
          const clearBox = f.clearable && has ? checkbox("Remove the stored value", false) : null;
          clearBox?.input.addEventListener("change", () => { el.disabled = clearBox.input.checked; if (el.disabled) el.value = ""; });
          controls.set(f.key, { secret: true, get: () => el.value, clears: () => !!clearBox?.input.checked, reset: () => { el.value = ""; } });
          const wrap = field(f.label, el, {
            help: f.help,
            required: f.required && !has,
            extra: has ? h("span", { class: "tag-stored" }, icon("shield"), " encrypted") : null,
          });
          if (clearBox) wrap.append(clearBox);
          return wrap;
        }
        const el = f.type === "multiline"
          ? h("textarea", { class: "textarea", rows: 3, value: values[f.key] || "", placeholder: f.placeholder || "" })
          : input({
            type: f.type === "number" ? "number" : f.type === "url" ? "url" : "text",
            value: values[f.key] || "",
            placeholder: f.placeholder || "",
            autocomplete: "off",
            spellcheck: "false",
            inputmode: f.type === "number" ? "numeric" : null,
          });
        el.addEventListener("input", () => { values[f.key] = el.value; });
        controls.set(f.key, { get: () => el.value });
        return field(f.label, el, { help: f.help, required: f.required });
      }

      const status = h("div", { class: "stack" });
      let pairingId = null;

      const collect = () => {
        const body = { name: nameInput.value, kind: kind.kind, enabled: enabled.input.checked, values: {}, secrets: {}, clear_secrets: [] };
        for (const [key, control] of controls) {
          if (control.secret) {
            const v = control.get();
            if (v) body.secrets[key] = v;
            if (control.clears()) body.clear_secrets.push(key);
          } else {
            body.values[key] = control.get();
          }
        }
        if (pairingId) body.pairing_id = pairingId;
        return body;
      };

      const saveButton = button(profile ? "Save changes" : "Save channel", { variant: "primary" });
      const testButton = button("Send test", { iconName: "send", disabled: !profile, title: profile ? "Sends a test through the saved settings." : "Save the channel first." });
      const deleteButton = profile ? button("Delete", { variant: "danger", iconName: "trash" }) : null;

      saveButton.addEventListener("click", () => busy(saveButton, async () => {
        try {
          const saved = profile
            ? await api.put(`providers/${encodeURIComponent(profile.id)}`, collect())
            : await api.post("providers", collect());
          toast(`${saved.name} saved.`);
          await reload(saved.id);
          showEditor(saved.kind, saved);
          ctx.refreshOverview();
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      testButton.addEventListener("click", () => busy(testButton, async () => {
        try {
          const result = await api.post(`providers/${encodeURIComponent(profile.id)}/test`);
          status.replaceChildren(notice(result.message, result.succeeded ? "ok" : "danger", result.succeeded ? "check" : "alert"));
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      deleteButton?.addEventListener("click", async () => {
        const ok = await confirmDialog({
          title: `Delete ${profile.name}?`,
          message: "Its routes and delivery history go with it, and its stored credentials are erased. This cannot be undone.",
          confirmLabel: "Delete channel",
          danger: true,
        });
        if (!ok) return;
        await busy(deleteButton, async () => {
          try {
            await api.del(`providers/${encodeURIComponent(profile.id)}`);
            toast(`${profile.name} deleted.`);
            await reload(null);
            showPlaceholder();
            ctx.refreshOverview();
          } catch (error) {
            status.replaceChildren(notice(error.message, "danger"));
          }
        });
      });

      const pairingSection = kind.supports_pairing ? buildPairing() : null;

      function buildPairing() {
        const relay = profile?.relay;
        const state = h("div", { class: "stack" });
        const connect = button(relay?.connected ? "Reconnect" : "Connect", { iconName: "link" });
        const cancel = button("Cancel", { size: "sm", variant: "ghost" });
        cancel.hidden = true;
        let timer = null;
        let currentId = null;

        const idle = () => {
          state.replaceChildren(relay?.connected
            ? notice(`Connected${relay.relay_name ? ` to ${relay.relay_name}` : ""}. The credential is stored encrypted.`, "ok", "check")
            : notice("Not connected yet. Press Connect, then approve the short code on the hosted Relay at an.relay.dev.kabanitech.com.", null, "phone"));
        };
        idle();

        const stop = () => {
          if (timer) clearInterval(timer);
          timer = null;
          cancel.hidden = true;
          connect.disabled = false;
        };
        stopPairing = () => {
          if (currentId && !pairingId) api.del(`relay/pairings/${currentId}`).catch(() => {});
          stop();
        };

        const show = (snapshot) => {
          if (snapshot.status === "waiting") {
            state.replaceChildren(
              h("p", { class: "small muted", text: "Approve this code on the Relay's approval page. It is signed in with your Relay account." }),
              h("div", { class: "user-code", "aria-live": "polite", text: snapshot.user_code }),
              h("div", { class: "row-between" },
                h("a", { class: "btn btn-primary", href: snapshot.verification_uri_complete, target: "_blank", rel: "noopener noreferrer" }, icon("external"), "Open approval page"),
                h("span", { class: "small muted", text: `${snapshot.message} ${Math.floor(snapshot.remaining_seconds / 60)}:${String(snapshot.remaining_seconds % 60).padStart(2, "0")} left` })));
            return;
          }
          if (snapshot.status === "verifying") {
            state.replaceChildren(notice(snapshot.message, "info", "clock"));
            return;
          }
          stop();
          if (snapshot.status === "approved") {
            pairingId = snapshot.id;
            state.replaceChildren(notice(`${snapshot.reconnected ? "Reconnected" : "Connected"} as ${snapshot.connected_as}. ${snapshot.message}`,
              snapshot.active_device_count === 0 ? "warn" : "ok", "check"));
            saveButton.focus();
          } else {
            state.replaceChildren(notice(snapshot.message, snapshot.status === "cancelled" ? null : "danger"));
          }
        };

        connect.addEventListener("click", () => busy(connect, async () => {
          const body = collect();
          try {
            const snapshot = await api.post("relay/pairings", {
              sender_name: body.values.sender_name,
              provider_id: profile?.id,
            });
            currentId = snapshot.id;
            pairingId = null;
            show(snapshot);
            cancel.hidden = false;
            timer = setInterval(async () => {
              if (!ctx.isCurrent()) { stop(); return; }
              try { show(await api.get(`relay/pairings/${currentId}`)); } catch (error) { stop(); state.replaceChildren(notice(error.message, "danger")); }
            }, 2000);
          } catch (error) {
            state.replaceChildren(notice(error.message, "danger"));
          }
        }).then(() => { if (timer) connect.disabled = true; }));

        cancel.addEventListener("click", async () => {
          if (currentId) await api.del(`relay/pairings/${currentId}`).catch(() => {});
          stop();
          idle();
        });

        return h("div", { class: "stack" },
          h("div", { class: "row-between" }, h("strong", { text: "Connection" }), h("div", { class: "row" }, cancel, connect)),
          state);
      }

      return card({
        title: profile ? profile.name : `New ${kind.display_name} channel`,
        description: profile ? `${kind.display_name} · updated ${timeAgo(profile.updated_at)}` : kind.summary,
        actions: [kind.paid ? badge("Paid service", "warn") : null, profile ? null : button("Back", { size: "sm", onClick: showPicker })],
        body: [
          kind.warning ? notice(kind.warning, "warn") : null,
          h("div", { class: "fields-2" }, field("Name", nameInput, { required: true }), h("div", { class: "field" }, h("span", { class: "field-label", text: "Status" }), enabled)),
          h("hr", { class: "divider" }),
          main,
          pairingSection ? h("hr", { class: "divider" }) : null,
          pairingSection,
          advancedBody.childElementCount ? advanced : null,
          ...(kind.notes || []).map((text) => h("p", { class: "small muted", text })),
          status,
        ],
        footer: [deleteButton, h("span", { class: "grow" }), testButton, saveButton],
      });
    }

    drawList();
    const initial = selectedId && providers.find((p) => p.id === selectedId);
    if (initial) showEditor(initial.kind, initial);
    else if (providers.length === 0) showPicker();
    else showPlaceholder();

    return () => { if (stopPairing) stopPairing(); };
  },
};

