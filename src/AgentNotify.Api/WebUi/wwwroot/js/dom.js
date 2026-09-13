// DOM helpers. Every piece of text goes through textContent: notification titles, prompts, and
// provider names are written by agents and users, and must never become markup.

const PROPERTIES = new Set(["value", "checked", "disabled", "selected", "indeterminate", "open", "multiple"]);

export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) {
    for (const [key, value] of Object.entries(props)) {
      if (value === undefined || value === null || value === false) continue;
      if (key === "class") el.className = value;
      else if (key === "text") el.textContent = value;
      else if (key === "style") for (const [name, v] of Object.entries(value)) el.style.setProperty(name, v);
      else if (key.startsWith("on") && typeof value === "function") el.addEventListener(key.slice(2).toLowerCase(), value);
      else if (PROPERTIES.has(key)) el[key] = value;
      else el.setAttribute(key, value === true ? "" : String(value));
    }
  }
  append(el, children);
  return el;
}

export function append(parent, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    parent.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return parent;
}

export function clear(el) {
  while (el.firstChild) el.firstChild.remove();
  return el;
}

/** Replaces an element's content. Unlike the native append, null and false children are skipped. */
export function mount(el, ...children) {
  return append(clear(el), children);
}

// ---- icons (24px stroke glyphs) ------------------------------------------------------------

const ICONS = {
  home: "M3 10.5 12 3l9 7.5V21a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z",
  bell: "M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9|M10.3 21a1.94 1.94 0 0 0 3.4 0",
  question: "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z|M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3|M12 17h.01",
  send: "m22 2-7 20-4-9-9-4z|M22 2 11 13",
  route: "M6 19a3 3 0 1 0 0-6 3 3 0 0 0 0 6z|M18 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6z|M9 16h5a4 4 0 0 0 4-4v-1",
  sliders: "M4 21v-7|M4 10V3|M12 21v-9|M12 8V3|M20 21v-5|M20 12V3|M1 14h6|M9 8h6|M17 16h6",
  volume: "M11 5 6 9H2v6h4l5 4z|M15.5 8.5a5 5 0 0 1 0 7|M19 5a10 10 0 0 1 0 14",
  bot: "M12 8V4H8|M4 8h16v12H4z|M2 14h2|M20 14h2|M15 13v2|M9 13v2",
  info: "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z|M12 16v-4|M12 8h.01",
  check: "M20 6 9 17l-5-5",
  x: "M18 6 6 18|M6 6l12 12",
  alert: "M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z|M12 9v4|M12 17h.01",
  plus: "M12 5v14|M5 12h14",
  trash: "M3 6h18|M8 6V4h8v2|M19 6l-1 14H6L5 6",
  copy: "M8 8h12v12H8z|M4 16V4h12",
  play: "M6 4l14 8-14 8z",
  upload: "M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4|M17 8l-5-5-5 5|M12 3v12",
  refresh: "M21 12a9 9 0 1 1-3-6.7L21 8|M21 3v5h-5",
  menu: "M4 6h16|M4 12h16|M4 18h16",
  logout: "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4|M16 17l5-5-5-5|M21 12H9",
  moon: "M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z",
  sun: "M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10z|M12 1v2|M12 21v2|M4.2 4.2l1.4 1.4|M18.4 18.4l1.4 1.4|M1 12h2|M21 12h2|M4.2 19.8l1.4-1.4|M18.4 5.6l1.4-1.4",
  external: "M15 3h6v6|M10 14 21 3|M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6",
  link: "M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7|M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7",
  shield: "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z",
  inbox: "M22 12h-6l-2 3h-4l-2-3H2|M5.5 5.1 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1z",
  pulse: "M22 12h-4l-3 9L9 3l-3 9H2",
  terminal: "M4 17l6-6-6-6|M12 19h8",
  phone: "M7 2h10a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z|M12 18h.01",
  clock: "M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z|M12 6v6l4 2",
  tag: "M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8z|M7.5 7.5h.01",
};

export function icon(name, extraClass) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "2");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  if (extraClass) svg.setAttribute("class", extraClass);
  for (const d of (ICONS[name] || ICONS.info).split("|")) {
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", d);
    svg.append(path);
  }
  return svg;
}

export function brandMark() {
  const img = h("img", { src: "favicon.svg", alt: "", class: "brand-mark", width: 30, height: 30 });
  return img;
}

// ---- building blocks -----------------------------------------------------------------------

export function button(label, { variant, size, iconName, onClick, type = "button", disabled, title } = {}) {
  const classes = ["btn"];
  if (variant) classes.push(`btn-${variant}`);
  if (size) classes.push(`btn-${size}`);
  if (!label) classes.push("btn-icon");
  return h("button", { type, class: classes.join(" "), onClick, disabled, title, "aria-label": label ? null : title },
    iconName ? icon(iconName) : null, label || null);
}

/** Runs an async action with the button disabled and a spinner showing. */
export async function busy(btn, action) {
  if (btn.disabled) return;
  btn.disabled = true;
  btn.classList.add("is-busy");
  try {
    return await action();
  } finally {
    btn.disabled = false;
    btn.classList.remove("is-busy");
  }
}

export function card({ title, description, actions, body, footer, className } = {}) {
  const head = title || actions
    ? h("div", { class: "card-head" },
        h("div", null, title ? h("h2", { class: "card-title", text: title }) : null,
          description ? h("p", { class: "card-desc", text: description }) : null),
        actions ? h("div", { class: "row" }, actions) : null)
    : null;
  return h("section", { class: `card ${className || ""}` }, head,
    body ? h("div", { class: "card-body" }, body) : null,
    footer ? h("div", { class: "card-foot" }, footer) : null);
}

export function pageHead(title, description, actions) {
  return h("header", { class: "page-head" },
    h("div", null, h("h1", { class: "page-title", text: title }), description ? h("p", { class: "page-desc", text: description }) : null),
    actions ? h("div", { class: "page-actions" }, actions) : null);
}

export function badge(text, tone) {
  return h("span", { class: `badge${tone ? ` badge-${tone}` : ""}`, text });
}

export function notice(text, tone, iconName) {
  return h("div", { class: `notice${tone ? ` notice-${tone}` : ""}`, role: tone === "danger" ? "alert" : null },
    icon(iconName || (tone === "warn" || tone === "danger" ? "alert" : "info")),
    h("div", null, text));
}

export function empty(title, text, iconName = "inbox") {
  return h("div", { class: "empty" }, icon(iconName), h("p", { class: "empty-title", text: title }), text ? h("p", { text }) : null);
}

let fieldCounter = 0;
export function uid(prefix = "f") {
  fieldCounter += 1;
  return `${prefix}-${fieldCounter}`;
}

export function field(label, control, { help, required, extra } = {}) {
  if (!control.id) control.id = uid();
  return h("div", { class: "field" },
    h("label", { class: "field-label", for: control.id }, label, required ? h("span", { class: "req", text: "*", "aria-hidden": "true" }) : null, extra || null),
    control,
    help ? h("p", { class: "field-help", text: help }) : null);
}

export function input(props = {}) {
  return h("input", { class: "input", type: "text", ...props });
}

export function select(options, value, props = {}) {
  const el = h("select", { class: "select", ...props });
  for (const option of options) {
    const [v, label] = Array.isArray(option) ? option : [option.value, option.label];
    el.append(h("option", { value: v, text: label, selected: String(v) === String(value) }));
  }
  return el;
}

export function checkbox(label, checked, { help, onChange } = {}) {
  const box = h("input", { type: "checkbox", checked: !!checked, onChange });
  const el = h("label", { class: "check" }, box, h("span", { class: "check-text" }, h("span", { text: label }), help ? h("span", { class: "check-help", text: help }) : null));
  el.input = box;
  return el;
}

export function toggle(label, checked, { help, onChange } = {}) {
  const box = h("input", { type: "checkbox", role: "switch", checked: !!checked, onChange });
  const el = h("label", { class: "switch" }, box, h("span", { class: "switch-track", "aria-hidden": "true" }),
    h("span", { class: "check-text" }, h("span", { text: label }), help ? h("span", { class: "check-help", text: help }) : null));
  el.input = box;
  return el;
}

export function codeLine(text) {
  const copy = button("", { variant: "ghost", size: "sm", iconName: "copy", title: "Copy" });
  copy.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(text);
      toast("Copied to the clipboard.");
    } catch {
      toast("Copy failed. Select the text instead.", "error");
    }
  });
  return h("div", { class: "code" }, h("span", { class: "code-text", text }), copy);
}

export function tabs(items, selected, onSelect) {
  const el = h("div", { class: "tabs", role: "tablist" });
  for (const item of items) {
    const tab = h("button", {
      type: "button",
      class: "tab",
      role: "tab",
      "aria-selected": String(item.id === selected),
      onClick: () => {
        for (const other of el.children) other.setAttribute("aria-selected", "false");
        tab.setAttribute("aria-selected", "true");
        onSelect(item.id);
      },
    }, item.label, item.count ? h("span", { class: "badge badge-solid", text: String(item.count) }) : null);
    el.append(tab);
  }
  return el;
}

// ---- feedback ------------------------------------------------------------------------------

let toastHost;
export function toast(message, tone = "ok") {
  if (!toastHost) {
    toastHost = h("div", { class: "toasts", role: "status", "aria-live": "polite" });
    document.body.append(toastHost);
  }
  const el = h("div", { class: `toast toast-${tone}` }, icon(tone === "error" ? "alert" : "check"), h("div", { text: message }));
  toastHost.append(el);
  setTimeout(() => el.remove(), tone === "error" ? 7000 : 3500);
}

/** A promise-based confirmation dialog. Browser dialogs are avoided: they block the page. */
export function confirmDialog({ title, message, confirmLabel = "Confirm", danger = false }) {
  return new Promise((resolve) => {
    const previous = document.activeElement;
    const close = (result) => {
      scrim.remove();
      document.removeEventListener("keydown", onKey);
      if (previous instanceof HTMLElement) previous.focus();
      resolve(result);
    };
    const onKey = (event) => { if (event.key === "Escape") close(false); };
    const ok = button(confirmLabel, { variant: danger ? "danger" : "primary", onClick: () => close(true) });
    const scrim = h("div", { class: "modal-scrim", onClick: (event) => { if (event.target === scrim) close(false); } },
      h("div", { class: "card modal", role: "alertdialog", "aria-modal": "true", "aria-labelledby": "modal-title" },
        h("h2", { class: "card-title", id: "modal-title", text: title }),
        h("p", { class: "muted", text: message }),
        h("div", { class: "modal-actions" }, button("Cancel", { onClick: () => close(false) }), ok)));
    document.body.append(scrim);
    document.addEventListener("keydown", onKey);
    ok.focus();
  });
}

// ---- formatting ----------------------------------------------------------------------------

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });

export function timeAgo(value) {
  if (!value) return "";
  const seconds = Math.round((new Date(value).getTime() - Date.now()) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 45) return seconds <= 0 ? "just now" : "in a moment";
  if (abs < 3600) return relative.format(Math.round(seconds / 60), "minute");
  if (abs < 86400) return relative.format(Math.round(seconds / 3600), "hour");
  return relative.format(Math.round(seconds / 86400), "day");
}

export function fullTime(value) {
  return value ? new Date(value).toLocaleString() : "";
}

export function duration(totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds));
  const d = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d) return `${d}d ${hours}h`;
  if (hours) return `${hours}h ${m}m`;
  if (m) return `${m}m`;
  return `${s}s`;
}

export function humanize(id) {
  const text = String(id || "").replace(/_/g, " ");
  return text.charAt(0).toUpperCase() + text.slice(1);
}
