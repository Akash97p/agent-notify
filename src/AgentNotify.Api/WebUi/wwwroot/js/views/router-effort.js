import { api } from "../api.js";
import { h, mount, pageHead, card, field, input, button, busy, notice, toast, badge } from "../dom.js";

const LABELS = ["Low", "Medium", "High", "Extra", "Max"];

export default {
  async render(page) {
    let data;
    try { data = await api.get("router/effort-mappings"); }
    catch (error) {
      mount(page, pageHead("Effort mapping", "Map Claude Code's effort levels onto each provider model."), notice(error.message, "danger"));
      return;
    }

    const host = h("div", { class: "stack" });
    mount(page,
      pageHead("Effort mapping", "Automatic provider/model capability mapping, editable when a provider or aggregator differs."),
      notice("Claude Code sends Low, Medium, High, Extra, or Max. AgentNotify maps that value for each concrete target after routing. A target default is used when the request carries no effort. Automatic entries are curated or inferred, not provider-reported guarantees.", "info"),
      host);

    const draw = () => {
      host.replaceChildren(...(data.mappings || []).map(mapping => mappingCard(mapping)));
      if (!(data.mappings || []).length)
        host.append(card({ title: "No routed models", body: h("p", { class: "muted small", text: "Add a provider and select models first." }) }));
    };

    function mappingCard(mapping) {
      const supported = input({ value: mapping.supported_values.join(", "), placeholder: "low, medium, high" });
      const mapControls = mapping.level_map.map((value, index) =>
        field(LABELS[index], input({ value, placeholder: "supported value or omit" })));
      const defaultControl = input({ value: mapping.default_value || "", placeholder: "blank, omit, or a supported value" });
      const status = h("div", { class: "stack" });
      const save = button("Save override", { variant: "primary", size: "sm" });
      save.addEventListener("click", () => busy(save, async () => {
        try {
          await api.put("router/effort-mappings", {
            upstream_id: mapping.upstream_id,
            model: mapping.model,
            supported_values: supported.value.split(",").map(value => value.trim()).filter(Boolean),
            level_map: mapControls.map(item => item.querySelector("input").value.trim()),
            default_value: defaultControl.value || null,
          });
          toast("Effort mapping saved.");
          data = await api.get("router/effort-mappings");
          draw();
        } catch (error) { status.replaceChildren(notice(error.message, "danger")); }
      }));
      const reset = button("Reset to automatic", { size: "sm" });
      reset.disabled = mapping.source !== "override";
      reset.addEventListener("click", () => busy(reset, async () => {
        try {
          await api.del(`router/effort-mappings?upstream_id=${encodeURIComponent(mapping.upstream_id)}&model=${encodeURIComponent(mapping.model)}`);
          toast("Automatic effort mapping restored.");
          data = await api.get("router/effort-mappings");
          draw();
        } catch (error) { status.replaceChildren(notice(error.message, "danger")); }
      }));

      return card({
        title: `${mapping.upstream_slug}/${mapping.model}`,
        actions: badge(mapping.source === "override" ? "overridden" : mapping.source, mapping.source === "override" ? "warn" : "ok"),
        description: `${mapping.family} · ${mapping.wire}`,
        body: [
          field("Supported target values", supported, { help: "Comma-separated exact values accepted by this provider/model." }),
          h("div", { class: "grid-2" }, ...mapControls),
          field("Default when source sends no effort", defaultControl),
          h("div", { class: "row" }, save, reset),
          status,
        ],
      });
    }

    draw();
  },
};
