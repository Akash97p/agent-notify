import { api } from "../api.js";
import { h, mount, pageHead, card, field, input, button, busy, notice, toast, badge, tabs } from "../dom.js";

const LABELS = ["Low", "Medium", "High", "Extra", "Max"];

const FAMILY_NAMES = {
  claude: "Claude",
  openai: "OpenAI",
  deepseek: "DeepSeek",
  glm: "GLM",
  kimi: "Kimi",
  qwen: "Qwen",
  minimax: "MiniMax",
  grok: "Grok",
  muse: "Muse Code",
  unknown: "Other models",
};

let currentView = "simple";

export default {
  async render(page) {
    let data;
    try { data = await api.get("router/effort-mappings"); }
    catch (error) {
      mount(page, pageHead("Effort mapping", "Map effort levels onto each family of models."), notice(error.message, "danger"));
      return;
    }

    const host = h("div", { class: "stack" });
    const viewTabs = tabs(
      [
        { id: "simple", label: "Families", count: (data.families || []).length },
        { id: "advanced", label: "Per model", count: (data.mappings || []).length },
      ],
      currentView,
      (next) => { currentView = next; draw(); });

    mount(page,
      pageHead("Effort mapping", "One effort table per model family; a single model can still get its own."),
      notice("Claude Code and Codex both send Low, Medium, High, Extra, or Max. Each family's table says where those levels land on its own scale, and what to send when a request carries no effort. Automatic tables send every level the family can spell under its own name; edit a family here, or one exact model under Per model.", "info"),
      viewTabs,
      host);

    const draw = () => {
      if (currentView === "simple") drawFamilies();
      else drawMappings();
    };

    function drawFamilies() {
      const families = data.families || [];
      host.replaceChildren(...families.map(family => familyCard(family)));
      if (!families.length)
        host.append(card({ title: "No routed models", body: h("p", { class: "muted small", text: "Add a provider and select models first." }) }));
    }

    function drawMappings() {
      host.replaceChildren(...(data.mappings || []).map(mapping => mappingCard(mapping)));
      if (!(data.mappings || []).length)
        host.append(card({ title: "No routed models", body: h("p", { class: "muted small", text: "Add a provider and select models first." }) }));
    }

    function familyCard(family) {
      const overridden = family.source === "family";
      const supported = input({ value: family.supported_values.join(", "), placeholder: "low, medium, high" });
      const mapControls = family.level_map.map((value, index) =>
        field(LABELS[index], input({ value, placeholder: "supported value or omit" })));
      const defaultControl = input({ value: family.default_value || "", placeholder: "blank, omit, or a supported value" });
      const status = h("div", { class: "stack" });
      const save = button(overridden ? "Save family override" : "Override family", { variant: "primary", size: "sm" });
      save.addEventListener("click", () => busy(save, async () => {
        try {
          await api.put("router/effort-mappings", {
            family: family.family,
            supported_values: supported.value.split(",").map(value => value.trim()).filter(Boolean),
            level_map: mapControls.map(item => item.querySelector("input").value.trim()),
            default_value: defaultControl.value || null,
          });
          toast("Family effort mapping saved.");
          data = await api.get("router/effort-mappings");
          draw();
        } catch (error) { status.replaceChildren(notice(error.message, "danger")); }
      }));
      const reset = button("Reset to automatic", { size: "sm" });
      reset.disabled = !overridden;
      reset.addEventListener("click", () => busy(reset, async () => {
        try {
          await api.del(`router/effort-mappings?family=${encodeURIComponent(family.family)}`);
          toast("Automatic effort mapping restored.");
          data = await api.get("router/effort-mappings");
          draw();
        } catch (error) { status.replaceChildren(notice(error.message, "danger")); }
      }));

      const models = family.models || [];
      const providers = [...new Set(models.map(model => model.upstream_slug))];
      const modelList = h("details", { class: "meta-disclosure" },
        h("summary", { text: `Models covered (${models.length})` }),
        h("div", { class: "stack" },
          h("p", { class: "muted small", text: "Every routed model of this family, at any provider, follows this table until one of them gets its own override under Per model." }),
          ...models.map(model => h("div", { class: "mono small", text: `${model.upstream_slug}/${model.model}` }))));

      return card({
        title: FAMILY_NAMES[family.family] || family.family,
        actions: badge(overridden ? "overridden" : "automatic", overridden ? "warn" : "ok"),
        description: `${models.length} model${models.length === 1 ? "" : "s"} · ${providers.join(", ")}`,
        body: [
          field("Supported target values", supported, { help: "Comma-separated exact values this family accepts." }),
          h("div", { class: "grid-2" }, ...mapControls),
          field("Default when a request sends no effort", defaultControl),
          h("div", { class: "row" }, save, reset),
          family.model_override_count > 0
            ? notice(`${family.model_override_count} model${family.model_override_count === 1 ? " has its own override" : "s have their own overrides"} — see Per model.`, "info")
            : null,
          modelList,
          status,
        ],
      });
    }

    function mappingCard(mapping) {
      const sourceLabel = mapping.source === "override" ? "overridden"
        : mapping.source === "family" ? "family override" : mapping.source;
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
        actions: badge(sourceLabel, mapping.source === "override" || mapping.source === "family" ? "warn" : "ok"),
        description: `${mapping.family} · ${mapping.wire}`,
        body: [
          field("Supported target values", supported, { help: "Comma-separated exact values accepted by this provider/model." }),
          h("div", { class: "grid-2" }, ...mapControls),
          field("Default when a request sends no effort", defaultControl),
          h("div", { class: "row" }, save, reset),
          status,
        ],
      });
    }

    draw();
  },
};
