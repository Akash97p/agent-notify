import { api } from "../api.js";
import { h, mount, clear, pageHead, badge, button, busy, empty, tabs, toast, timeAgo, fullTime, humanize, card, notice, uid } from "../dom.js";

function remaining(expiresAt) {
  const seconds = Math.round((new Date(expiresAt).getTime() - Date.now()) / 1000);
  if (seconds <= 0) return "expiring";
  if (seconds < 60) return `${seconds}s left`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s left`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m left`;
}

const STATUS_TONES = { answered: "ok", expired: "warn", cancelled: null, superseded: null, pending: "info" };

function answerText(q) {
  if (!q.response) return null;
  if (q.kind === "text") return q.response.text || "";
  const choice = q.choices.find((c) => c.id === q.response.choice_id);
  return choice ? choice.label : q.response.choice_id;
}

function questionItem(q, onChanged) {
  const pending = q.status === "pending";
  const status = h("span", { class: "small muted nowrap", text: pending ? remaining(q.expires_at) : timeAgo(q.answered_at || q.updated_at) });
  const error = h("div", { hidden: true });

  const send = async (btn, payload) => busy(btn, async () => {
    try {
      await api.post(`interactions/${encodeURIComponent(q.id)}/respond`, { request_digest: q.request_digest, ...payload });
      toast("Answer sent. The agent receives it the next time it checks.");
      onChanged();
    } catch (e) {
      error.hidden = false;
      error.replaceChildren(notice(e.status === 409 ? "Someone already answered this question." : e.message, "danger"));
      if (e.status === 409 || e.status === 404) onChanged();
    }
  });

  let controls = null;
  if (pending && (q.kind === "permission" || (q.kind === "single_choice" && q.choices.length <= 3 && q.choices.every((c) => !c.detail)))) {
    controls = h("div", { class: "item-actions" }, q.choices.map((choice, index) => {
      const deny = /deny|reject|no|cancel|block/i.test(choice.id);
      const btn = button(choice.label, { variant: index === 0 && !deny ? "primary" : deny ? "danger" : null, title: choice.detail || null });
      btn.addEventListener("click", () => send(btn, { choice_id: choice.id }));
      return btn;
    }));
  } else if (pending && q.kind === "single_choice") {
    const name = uid("choice");
    const submit = button("Send answer", { variant: "primary", disabled: true });
    const options = h("div", { class: "choice-list", role: "radiogroup" }, q.choices.map((choice) => {
      const radio = h("input", { type: "radio", name, value: choice.id, onChange: () => { submit.disabled = false; } });
      return h("label", { class: "choice" }, radio,
        h("span", { class: "check-text" }, h("strong", { text: choice.label }), choice.detail ? h("span", { class: "check-help", text: choice.detail }) : null));
    }));
    submit.addEventListener("click", () => {
      const picked = options.querySelector("input:checked");
      if (picked) send(submit, { choice_id: picked.value });
    });
    controls = h("div", { class: "stack" }, options, h("div", { class: "item-actions" }, submit));
  } else if (pending && q.kind === "text") {
    const max = q.text_max_length || 500;
    const counter = h("span", { class: "small muted", text: `0 / ${max}` });
    const area = h("textarea", { class: "textarea", maxlength: max, rows: 3, placeholder: "Type your answer", "aria-label": "Your answer" });
    const submit = button("Send answer", { variant: "primary", disabled: true });
    area.addEventListener("input", () => {
      counter.textContent = `${area.value.length} / ${max}`;
      submit.disabled = area.value.trim().length === 0;
    });
    area.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && (event.metaKey || event.ctrlKey) && !submit.disabled) submit.click();
    });
    submit.addEventListener("click", () => send(submit, { text: area.value }));
    controls = h("div", { class: "stack" }, area, h("div", { class: "row-between" }, counter, submit));
  }

  const cancel = pending ? button("Withdraw", { size: "sm", variant: "ghost", title: "Cancel this question. The agent is told nobody will answer it." }) : null;
  cancel?.addEventListener("click", () => busy(cancel, async () => {
    try {
      await api.post(`interactions/${encodeURIComponent(q.id)}/cancel`);
      toast("Question withdrawn.");
      onChanged();
    } catch (e) {
      toast(e.message, "error");
    }
  }));

  const answered = answerText(q);
  const item = h("article", { class: "card item", style: { "--accent-line": pending ? "var(--info)" : "var(--border)" } },
    h("div", { class: "item-top" },
      badge(q.kind === "single_choice" ? "Choice" : humanize(q.kind), q.kind === "permission" ? "danger" : "info"),
      badge(humanize(q.status), STATUS_TONES[q.status]),
      h("span", { class: "grow" }),
      status,
      cancel),
    h("h3", { class: "item-title", text: q.prompt }),
    h("div", { class: "item-meta" },
      h("span", { text: `Agent: ${q.agent}${q.agent_instance ? ` (${q.agent_instance})` : ""}` }),
      q.project ? h("span", { text: `Project: ${q.project}` }) : null,
      h("time", { datetime: q.created_at, title: fullTime(q.created_at), text: `Asked ${timeAgo(q.created_at)}` })),
    controls,
    error,
    answered !== null ? h("div", { class: "notice notice-ok" },
      h("div", null, h("strong", { text: "Answer: " }), h("span", { text: answered }),
        h("span", { class: "small", text: ` · from ${q.response.source}${q.response.device_id ? ` (${q.response.device_id})` : ""}` }))) : null);

  item.tick = () => { if (pending) status.textContent = remaining(q.expires_at); };
  return item;
}

export default {
  async render(page, ctx) {
    const o = ctx.overview();
    if (o && !o.capabilities.questions) {
      mount(page, pageHead("Questions"), notice("This broker was started without interactions, so agents cannot ask questions here.", "warn"));
      return;
    }

    let view = ctx.params.get("view") === "recent" ? "recent" : "pending";
    const list = h("div", { class: "feed" });
    let items = [];
    let signature = "";

    const load = async (force) => {
      const next = await api.get(`interactions?view=${view}`);
      if (!ctx.isCurrent()) return;
      // Redraw only when something changed, so a half-typed answer is never wiped by a refresh.
      const nextSignature = JSON.stringify(next.map((q) => [q.id, q.status, q.updated_at]));
      if (!force && nextSignature === signature) return;
      signature = nextSignature;
      clear(list);
      items = [];
      if (next.length === 0) {
        list.append(card({ body: view === "pending"
          ? empty("No agent is waiting on you", "When an agent asks a question or needs approval, you can answer it here, on your phone, or wherever it reaches you first.", "check")
          : empty("No answered questions yet") }));
        return;
      }
      for (const q of next) {
        const item = questionItem(q, () => { load(true); ctx.refreshOverview(); });
        items.push(item);
        list.append(item);
      }
    };

    mount(page, 
      pageHead("Questions", "Agents that are waiting for a decision. The first answer from any surface wins; the rest are refused.",
        h("div", { class: "row" },
          tabs([{ id: "pending", label: "Waiting" }, { id: "recent", label: "All recent" }], view, (next) => { view = next; load(true); }),
          button("", { iconName: "refresh", title: "Refresh", onClick: () => load(true) }))),
      list);

    await load(true);
    const poll = setInterval(() => { if (document.visibilityState === "visible") load(false).catch(() => {}); }, 4000);
    const tick = setInterval(() => items.forEach((item) => item.tick()), 1000);
    return () => { clearInterval(poll); clearInterval(tick); };
  },
};
