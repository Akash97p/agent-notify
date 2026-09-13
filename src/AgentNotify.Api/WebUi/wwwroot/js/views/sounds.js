import { api, request } from "../api.js";
import { h, mount, clear, pageHead, button, busy, toast, card, notice, select, toggle, humanize } from "../dom.js";

export default {
  async render(page, ctx) {
    const [settings, library, types, overview] = await Promise.all([api.get("settings"), api.get("sounds"), api.get("types"), ctx.refreshOverview()]);
    let lib = library;
    let defaultSound = settings.default_sound_file || "";
    let typeSounds = { ...settings.type_sound_files };
    const audio = new Audio();

    const enabled = toggle("Play notification sounds", settings.sounds_enabled);
    const critical = toggle("Allow critical sounds during Do not disturb", settings.play_critical_sounds_during_do_not_disturb);
    const volumeValue = h("span", { class: "small muted", text: `${settings.sound_volume}%` });
    const volume = h("input", { type: "range", class: "range", min: 0, max: 100, value: settings.sound_volume, "aria-label": "Volume" });
    volume.addEventListener("input", () => { volumeValue.textContent = `${volume.value}%`; audio.volume = Number(volume.value) / 100; });

    const options = (includeGlobal) => [
      ["", includeGlobal ? "Use the global sound" : "No sound"],
      ...lib.built_in.map((t) => [t.file_name, `${t.display_name} (built in)`]),
      ...lib.imported.map((file) => [file, file]),
    ];

    const preview = (file, btn) => {
      if (!file) { toast("Choose a sound to preview.", "error"); return; }
      audio.pause();
      audio.src = `api/sounds/${encodeURIComponent(file)}`;
      audio.volume = Number(volume.value) / 100;
      audio.play().catch(() => toast("That sound is not available on this computer. Built-in tones are installed with the Windows app.", "error"));
      btn?.blur();
    };

    const globalSelect = select(options(false), defaultSound, { id: "global-sound" });
    globalSelect.addEventListener("change", () => { defaultSound = globalSelect.value; });
    const globalPreview = button("", { iconName: "play", title: "Preview", onClick: (e) => preview(globalSelect.value, e.currentTarget) });

    const upload = h("input", { type: "file", accept: ".wav,.mp3,audio/wav,audio/mpeg", class: "sr-only", id: "sound-upload" });
    const uploadButton = button("Upload WAV or MP3", { iconName: "upload", onClick: () => upload.click() });
    upload.addEventListener("change", () => busy(uploadButton, async () => {
      const file = upload.files?.[0];
      upload.value = "";
      if (!file) return;
      if (file.size > 10 * 1024 * 1024) { toast("Sound files must be at most 10 MB.", "error"); return; }
      const form = new FormData();
      form.append("file", file);
      try {
        const result = await request("POST", "sounds", form);
        lib = await api.get("sounds");
        globalSelect.replaceChildren(...select(options(false), result.file_name).children);
        globalSelect.value = result.file_name;
        defaultSound = result.file_name;
        drawOverrides();
        toast(`${result.file_name} added. Save to use it.`);
      } catch (error) {
        toast(error.message, "error");
      }
    }));

    // ---- per-type overrides ----------------------------------------------------------------
    const allTypes = [...types.built_in.map((t) => [t, humanize(t)]), ...types.custom.map((t) => [t.id, t.display_name])];
    const overridesBody = h("div", { class: "stack" });
    function drawOverrides() {
      clear(overridesBody);
      const rows = allTypes.map(([id, label]) => {
        const choice = select(options(true), typeSounds[id] || "");
        choice.addEventListener("change", () => {
          if (choice.value) typeSounds[id] = choice.value; else delete typeSounds[id];
        });
        return h("tr", null,
          h("td", null, h("strong", { text: label })),
          h("td", null, choice),
          h("td", null, button("", { size: "sm", variant: "ghost", iconName: "play", title: `Preview ${label}`, onClick: (e) => preview(choice.value || globalSelect.value, e.currentTarget) })));
      });
      overridesBody.append(h("div", { class: "table-wrap" }, h("table", { class: "table" },
        h("thead", null, h("tr", null, h("th", { text: "Type" }), h("th", { text: "Sound" }), h("th", { text: "" }))),
        h("tbody", null, rows))));
    }
    drawOverrides();

    const status = h("div");
    const save = button("Save sounds", { variant: "primary" });
    save.addEventListener("click", () => busy(save, async () => {
      try {
        const result = await api.put("settings", {
          sounds_enabled: enabled.input.checked,
          sound_volume: Number(volume.value),
          play_critical_sounds_during_do_not_disturb: critical.input.checked,
          default_sound_file: defaultSound,
          type_sound_files: typeSounds,
        });
        typeSounds = { ...result.settings.type_sound_files };
        status.replaceChildren(notice("Saved.", "ok", "check"));
        toast("Sounds saved.");
      } catch (error) {
        status.replaceChildren(notice(error.message, "danger"));
      }
    }));

    mount(page, 
      pageHead("Sounds", "What a new notification sounds like, globally and for each type.", save),
      overview && !overview.capabilities.sounds
        ? notice(`Sounds are played by the Windows app. This broker shows notifications through ${overview.desktop_surface || "the platform"}, so these settings are kept for when it runs on Windows.`, "info")
        : null,
      card({
        title: "Playback",
        body: [
          enabled,
          h("div", { class: "field" }, h("div", { class: "row-between" }, h("span", { class: "field-label", text: "Volume" }), volumeValue), volume),
          critical,
        ],
      }),
      card({
        title: "Global sound",
        description: "Played for every type without its own sound.",
        body: [
          h("div", { class: "field" }, h("label", { class: "field-label", for: "global-sound", text: "Sound" }), h("div", { class: "input-row" }, globalSelect, globalPreview)),
          h("div", { class: "row" }, uploadButton, upload, h("span", { class: "small muted", text: "Imported files are copied into AgentNotify's data folder." })),
        ],
      }),
      card({ title: "By type", description: "Give the types you care about a sound of their own.", body: overridesBody }),
      status);

    return () => { audio.pause(); };
  },
};

