import { api } from "../api.js";
import { h, mount, clear, pageHead, badge, button, busy, toast, card, notice, field, input, select, toggle, humanize, confirmDialog, empty } from "../dom.js";

const PRIORITIES = [["low", "Low"], ["normal", "Normal"], ["high", "High"], ["critical", "Critical"]];

export default {
  async render(page, ctx) {
    const [settings, types, overview] = await Promise.all([api.get("settings"), api.get("types"), ctx.refreshOverview()]);
    const capabilities = overview?.capabilities || { toast_placement: true };

    // ---- general ---------------------------------------------------------------------------
    const port = input({ type: "number", min: 1, max: 65535, value: settings.port, inputmode: "numeric" });
    const retention = input({ type: "number", min: 0, max: 3650, value: settings.history_retention_days, inputmode: "numeric" });
    const pause = toggle("Pause desktop notifications", settings.pause_notifications, { help: "Notifications are still stored, routed, and listed here." });
    const dnd = toggle("Do not disturb", settings.do_not_disturb, { help: "Silences sounds. Critical sounds can still play if allowed on the Sounds page." });
    const generalStatus = h("div");

    // ---- toasts ----------------------------------------------------------------------------
    const location = select([["BottomRight", "Bottom right"], ["TopRight", "Top right"]], settings.toast_location);
    const maxVisible = input({ type: "number", min: 1, max: 20, value: settings.max_visible_toasts, inputmode: "numeric" });
    const durations = new Map();
    const durationGrid = h("div", { class: "fields-4" });
    for (const [type, seconds] of Object.entries(settings.toast_durations)) {
      const box = input({ type: "number", min: 0, max: 86400, value: seconds, inputmode: "numeric" });
      durations.set(type, box);
      durationGrid.append(field(humanize(type), box));
    }

    const saveSettings = async (btn, statusEl) => busy(btn, async () => {
      const toastDurations = {};
      for (const [type, box] of durations) toastDurations[type] = Number(box.value);
      try {
        const result = await api.put("settings", {
          port: Number(port.value),
          history_retention_days: Number(retention.value),
          pause_notifications: pause.input.checked,
          do_not_disturb: dnd.input.checked,
          toast_location: location.value,
          max_visible_toasts: Number(maxVisible.value),
          toast_durations: toastDurations,
        });
        statusEl.replaceChildren(result.restart_required
          ? notice(`Saved. Restart AgentNotify to move the local API to port ${result.settings.port}; agents keep using the old port until then.`, "warn")
          : notice("Saved.", "ok", "check"));
        toast("Settings saved.");
      } catch (error) {
        statusEl.replaceChildren(notice(error.message, "danger"));
      }
    });

    const generalSave = button("Save", { variant: "primary" });
    generalSave.addEventListener("click", () => saveSettings(generalSave, generalStatus));
    const toastStatus = h("div");
    const toastSave = button("Save", { variant: "primary" });
    toastSave.addEventListener("click", () => saveSettings(toastSave, toastStatus));

    // ---- custom types ----------------------------------------------------------------------
    let custom = types.custom.map((t) => ({ ...t }));
    const typesBody = h("div", { class: "stack" });
    const typesStatus = h("div");

    const persistTypes = async (next, btn) => busy(btn, async () => {
      try {
        const saved = await api.put("types", { custom: next });
        custom = saved.custom.map((t) => ({ ...t }));
        drawTypes();
        toast("Notification types saved.");
        return true;
      } catch (error) {
        typesStatus.replaceChildren(notice(error.message, "danger"));
        return false;
      }
    });

    function typeEditor(existing) {
      const id = input({ value: existing?.id || "", placeholder: "deploy_ready", disabled: !!existing, spellcheck: "false", autocomplete: "off" });
      const name = input({ value: existing?.display_name || "", placeholder: "Deploy ready", maxlength: 60 });
      const color = input({ type: "color", value: existing?.accent_color || "#4A90D9", class: "input", style: { padding: "2px", width: "64px" } });
      const priority = select(PRIORITIES, existing?.default_priority || "normal");
      const lifetime = input({ type: "number", min: 0, max: 86400, value: existing?.duration_seconds ?? 7, inputmode: "numeric" });
      const enabled = toggle("Enabled", existing ? existing.enabled : true);
      const save = button(existing ? "Save type" : "Add type", { variant: "primary" });
      const cancel = button("Cancel", { onClick: drawTypes });
      save.addEventListener("click", async () => {
        const entry = {
          id: id.value.trim(), display_name: name.value, accent_color: color.value, default_priority: priority.value,
          duration_seconds: Number(lifetime.value), enabled: enabled.input.checked,
        };
        const next = existing ? custom.map((t) => (t.id === existing.id ? entry : t)) : [...custom, entry];
        await persistTypes(next, save);
      });
      return card({
        className: "",
        title: existing ? `Edit ${existing.display_name}` : "New notification type",
        body: [
          h("div", { class: "fields-2" },
            field("Type ID", id, { required: true, help: existing ? "The ID is what agents send, so it cannot change." : "Lowercase letters, digits, and underscores. Agents pass it as --type." }),
            field("Display name", name)),
          h("div", { class: "fields-4" },
            field("Accent", color),
            field("Default priority", priority),
            field("Lifetime (s)", lifetime, { help: "0 keeps it until resolved." }),
            h("div", { class: "field" }, h("span", { class: "field-label", text: "Status" }), enabled)),
        ],
        footer: [cancel, save],
      });
    }

    function drawTypes() {
      typesStatus.replaceChildren();
      clear(typesBody);
      if (custom.length === 0) {
        typesBody.append(empty("No custom types", "Built-in types cover most agents. Add your own when a project needs its own label, colour, or lifetime.", "tag"));
      } else {
        const rows = custom.map((t) => {
          const edit = button("Edit", { size: "sm", onClick: () => typesBody.replaceChildren(typeEditor(t)) });
          const remove = button("", { size: "sm", variant: "ghost", iconName: "trash", title: `Delete ${t.display_name}` });
          remove.addEventListener("click", async () => {
            if (!await confirmDialog({ title: `Delete ${t.display_name}?`, message: "Existing notifications keep their type ID; they just lose this label and colour.", confirmLabel: "Delete type", danger: true })) return;
            await persistTypes(custom.filter((x) => x.id !== t.id), remove);
          });
          return h("tr", null,
            h("td", null, h("span", { class: "row" }, h("span", { class: "swatch", style: { background: t.accent_color } }), h("strong", { text: t.display_name }))),
            h("td", { class: "mono small", text: t.id }),
            h("td", { text: humanize(t.default_priority) }),
            h("td", { text: t.duration_seconds === 0 ? "Until resolved" : `${t.duration_seconds}s` }),
            h("td", null, t.enabled ? badge("On", "ok") : badge("Off")),
            h("td", null, h("div", { class: "row" }, edit, remove)));
        });
        typesBody.append(h("div", { class: "table-wrap" }, h("table", { class: "table" },
          h("thead", null, h("tr", null, ["Name", "ID", "Priority", "Lifetime", "Status", ""].map((t) => h("th", { text: t })))),
          h("tbody", null, rows))));
      }
      typesBody.append(h("div", null, button("Add type", { iconName: "plus", onClick: () => typesBody.replaceChildren(typeEditor(null)) })));
    }
    drawTypes();

    mount(page, 
      pageHead("Notifications", "How the broker keeps and shows notifications on this computer."),
      card({
        title: "General",
        body: [
          h("div", { class: "fields-2" },
            field("Local API port", port, { help: "Agents and the CLI connect here. Changing it takes effect after a restart." }),
            field("Keep history for (days)", retention, { help: "Resolved and dismissed notifications older than this are removed." })),
          pause, dnd, generalStatus,
        ],
        footer: generalSave,
      }),
      card({
        title: "Desktop toasts",
        description: "Where toasts appear and how long each type stays on screen.",
        body: [
          capabilities.toast_placement ? null : notice(`These settings apply to the Windows app. On this computer notifications are shown by ${overview?.desktop_surface || "the platform"}, which decides placement itself.`, "info"),
          h("div", { class: "fields-2" }, field("Screen corner", location), field("Most toasts at once", maxVisible, { help: "Extra toasts wait in a queue." })),
          h("div", null, h("p", { class: "field-label", text: "Lifetime by type, in seconds" }), h("p", { class: "field-help", text: "0 keeps a toast until it is resolved or dismissed." })),
          durationGrid, toastStatus,
        ],
        footer: toastSave,
      }),
      card({
        title: "Custom types",
        description: "Your own notification types, with a label, accent colour, default priority, and lifetime.",
        body: [typesBody, typesStatus],
      }));
  },
};
