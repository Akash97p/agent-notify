import { api } from "../api.js";
import { h, mount, clear, pageHead, badge, button, busy, empty, toast, card, notice, field, input, select, checkbox, toggle, confirmDialog, humanize, icon } from "../dom.js";

const PRIORITIES = [["low", "Low and above (everything)"], ["normal", "Normal and above"], ["high", "High and critical"], ["critical", "Critical only"]];

function statTile(label, value, tone) {
  return h("div", { class: "card stat" },
    h("span", { class: "stat-label", text: label }),
    h("span", { class: "stat-value", text: String(value), style: tone ? { color: `var(--${tone})` } : null }));
}

export default {
  async render(page, ctx) {
    const [initialRoutes, providers, types, delivery] = await Promise.all([
      api.get("routes"), api.get("providers"), api.get("types"), api.get("delivery"),
    ]);
    let routes = initialRoutes;
    let selectedId = null;
    const providerName = (id) => providers.find((p) => p.id === id)?.name || "Missing channel";

    const listBody = h("div", { class: "list" });
    const editorHost = h("div");
    const deliveryHost = h("div", { class: "stats" });

    const drawDelivery = (d) => {
      deliveryHost.replaceChildren(
        statTile("Delivered", d.delivered, "ok"),
        statTile("Queued", d.pending + d.processing),
        statTile("Retrying", d.retry, d.retry ? "warn" : null),
        statTile("Failed", d.dead_letter, d.dead_letter ? "danger" : null));
    };
    drawDelivery(delivery);

    mount(page, 
      pageHead("Routes", "A route sends matching new notifications to one channel. A notification can match several routes; each delivers once.",
        button("Add route", { variant: "primary", iconName: "plus", disabled: providers.length === 0, onClick: () => showEditor(null) })),
      providers.length === 0 ? notice(h("span", null, "Add a channel first. ", h("a", { href: "#/channels", text: "Open channels" })), "info") : null,
      deliveryHost,
      h("div", { class: "split" },
        h("div", { class: "sticky" }, card({ title: "Routes", body: listBody })),
        editorHost));

    const drawList = () => {
      clear(listBody);
      if (routes.length === 0) {
        listBody.append(empty("No routes yet", "Without a route, notifications stay on this computer.", "route"));
        return;
      }
      for (const r of routes) {
        const filters = [r.type_id ? humanize(r.type_id) : null, r.project, r.agent].filter(Boolean).join(" · ");
        listBody.append(h("button", { type: "button", class: "list-item", "aria-selected": String(r.id === selectedId), onClick: () => showEditor(r) },
          h("div", { class: "list-main" },
            h("div", { class: "list-title", text: r.name }),
            h("div", { class: "list-sub", text: `${providerName(r.provider_id)} · ${humanize(r.minimum_priority)}+${filters ? ` · ${filters}` : ""}` })),
          r.enabled ? badge("On", "ok") : badge("Off")));
      }
    };

    const reload = async (id) => {
      routes = await api.get("routes");
      selectedId = id;
      drawList();
      drawDelivery(await api.get("delivery"));
    };

    function showPlaceholder() {
      selectedId = null;
      drawList();
      editorHost.replaceChildren(card({ body: empty("Choose a route", providers.length ? "Select a route on the left, or add one." : "Routes need a channel to deliver to.", "route") }));
    }

    function showEditor(route) {
      selectedId = route ? route.id : null;
      drawList();
      const name = input({ value: route ? route.name : "", maxlength: 100, placeholder: "Phone for urgent approvals" });
      const provider = select(providers.map((p) => [p.id, `${p.name}${p.enabled ? "" : " (off)"}`]), route ? route.provider_id : providers[0]?.id);
      const enabled = toggle("Enabled", route ? route.enabled : true);
      const priority = select(PRIORITIES, route ? route.minimum_priority : "high");
      const typeOptions = [["", "Any type"], ...types.built_in.map((t) => [t, humanize(t)]), ...types.custom.map((t) => [t.id, t.display_name])];
      const type = select(typeOptions, route?.type_id || "");
      const project = input({ value: route?.project || "", maxlength: 100, placeholder: "Any project" });
      const agent = input({ value: route?.agent || "", maxlength: 100, placeholder: "Any agent" });
      const includeMessage = checkbox("Include the notification message", route ? route.include_message : true, {
        help: "Off sends only the title and type. Useful for chat rooms other people can read.",
      });
      const status = h("div");

      const save = button(route ? "Save changes" : "Save route", { variant: "primary" });
      save.addEventListener("click", () => busy(save, async () => {
        const body = {
          name: name.value, provider_id: provider.value, enabled: enabled.input.checked, minimum_priority: priority.value,
          type_id: type.value || null, project: project.value || null, agent: agent.value || null, include_message: includeMessage.input.checked,
        };
        try {
          const saved = route ? await api.put(`routes/${encodeURIComponent(route.id)}`, body) : await api.post("routes", body);
          toast(`${saved.name} saved.`);
          await reload(saved.id);
          showEditor(saved);
          ctx.refreshOverview();
        } catch (error) {
          status.replaceChildren(notice(error.message, "danger"));
        }
      }));

      const remove = route ? button("Delete", { variant: "danger", iconName: "trash" }) : null;
      remove?.addEventListener("click", async () => {
        if (!await confirmDialog({ title: `Delete ${route.name}?`, message: "Matching notifications stop being delivered through it.", confirmLabel: "Delete route", danger: true })) return;
        await busy(remove, async () => {
          try {
            await api.del(`routes/${encodeURIComponent(route.id)}`);
            toast("Route deleted.");
            await reload(null);
            showPlaceholder();
            ctx.refreshOverview();
          } catch (error) {
            status.replaceChildren(notice(error.message, "danger"));
          }
        });
      });

      editorHost.replaceChildren(card({
        title: route ? route.name : "New route",
        description: "Only notifications created after saving are routed.",
        body: [
          h("div", { class: "fields-2" }, field("Name", name, { required: true }), h("div", { class: "field" }, h("span", { class: "field-label", text: "Status" }), enabled)),
          field("Deliver to", provider, { required: true }),
          h("hr", { class: "divider" }),
          h("p", { class: "small muted" }, icon("sliders"), " Match notifications that are"),
          h("div", { class: "fields-2" },
            field("At least this priority", priority),
            field("Of this type", type),
            field("From this project", project, { help: "Exact project name. Blank matches every project." }),
            field("From this agent", agent, { help: "Exact agent name, such as claude or codex." })),
          includeMessage,
          status,
        ],
        footer: [remove, h("span", { class: "grow" }), save],
      }));
    }

    drawList();
    showPlaceholder();
  },
};
