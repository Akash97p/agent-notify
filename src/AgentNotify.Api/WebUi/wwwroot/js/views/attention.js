import { api } from "../api.js";
import { h, mount, clear, pageHead, badge, button, busy, empty, tabs, toast, timeAgo, fullTime, humanize, card } from "../dom.js";

const TYPE_TONES = {
  permission_required: ["#dc2626", "danger"],
  input_required: ["#d97706", "warn"],
  blocked: ["#d97706", "warn"],
  error: ["#dc2626", "danger"],
  warning: ["#d97706", "warn"],
  completed: ["#16a34a", "ok"],
  success: ["#16a34a", "ok"],
  info: ["#2563eb", "info"],
};

const PRIORITY_TONES = { critical: "danger", high: "warn", normal: null, low: null };

export function notificationItem(n, { onChanged, customTypes = [] } = {}) {
  const custom = customTypes.find((t) => t.id === n.type);
  const [accent, tone] = custom ? [custom.accent_color, null] : (TYPE_TONES[n.type] || ["#71717a", null]);
  const active = n.status === "active";

  const act = (action, label, variant) => {
    const btn = button(label, { size: "sm", variant });
    btn.addEventListener("click", () => busy(btn, async () => {
      try {
        await api.post(`notifications/${encodeURIComponent(n.id)}/${action}`);
        toast(action === "resolve" ? "Marked as resolved." : "Dismissed.");
        onChanged?.();
      } catch (error) {
        toast(error.message, "error");
      }
    }));
    return btn;
  };

  return h("article", { class: "card item", style: { "--accent-line": accent } },
    h("div", { class: "item-top" },
      badge(custom ? custom.display_name : humanize(n.type), tone),
      PRIORITY_TONES[n.priority] !== undefined && n.priority !== "normal" ? badge(humanize(n.priority), PRIORITY_TONES[n.priority]) : null,
      !active ? badge(humanize(n.status)) : null,
      h("span", { class: "grow" }),
      h("time", { class: "small muted", datetime: n.created_at, title: fullTime(n.created_at), text: timeAgo(n.created_at) })),
    h("h3", { class: "item-title", text: n.title }),
    n.message ? h("p", { class: "item-message", text: n.message }) : null,
    h("div", { class: "item-meta" },
      n.agent ? h("span", { text: `Agent: ${n.agent}${n.agent_instance ? ` (${n.agent_instance})` : ""}` }) : null,
      n.project ? h("span", { text: `Project: ${n.project}` }) : null,
      n.cwd ? h("span", { class: "mono truncate meta-path", title: n.cwd, text: n.cwd }) : null),
    active ? h("div", { class: "item-actions" }, act("resolve", "Resolve", "primary"), act("dismiss", "Dismiss")) : null);
}

export default {
  async render(page, ctx) {
    let view = ctx.params.get("view") === "recent" ? "recent" : "attention";
    const list = h("div", { class: "feed" });
    const types = await api.get("types").catch(() => ({ custom: [] }));

    const load = async () => {
      const items = await api.get(`notifications?view=${view}`);
      if (!ctx.isCurrent()) return;
      clear(list);
      if (items.length === 0) {
        list.append(card({ body: view === "attention"
          ? empty("Nothing is waiting on you", "When an agent needs a decision or hits a blocker, it shows up here until it is resolved.", "check")
          : empty("No history yet", "Notifications appear here as agents send them.") }));
        return;
      }
      for (const n of items) list.append(notificationItem(n, { customTypes: types.custom, onChanged: () => { load(); ctx.refreshOverview(); } }));
    };

    mount(page, 
      pageHead("Attention", "Notifications agents have sent. Active ones stay here until they are resolved or dismissed.",
        h("div", { class: "row" },
          tabs([{ id: "attention", label: "Needs attention" }, { id: "recent", label: "History" }], view, (next) => { view = next; load(); }),
          button("", { iconName: "refresh", title: "Refresh", onClick: load }))),
      list);

    await load();
    const timer = setInterval(() => { if (document.visibilityState === "visible") load().catch(() => {}); }, 8000);
    return () => clearInterval(timer);
  },
};
