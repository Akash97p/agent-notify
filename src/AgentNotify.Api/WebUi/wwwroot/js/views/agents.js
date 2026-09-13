import { api } from "../api.js";
import { h, mount, clear, pageHead, badge, button, busy, toast, card, notice, codeLine, confirmDialog } from "../dom.js";

const STATE = {
  up_to_date: ["Installed", "ok"],
  outdated: ["Different version", "warn"],
  not_installed: ["Not installed", null],
  unavailable: ["No home folder", "danger"],
};

export default {
  async render(page) {
    const draw = (data) => {
      const skills = data.skills.map((skill) => {
        const [label, tone] = STATE[skill.state] || [skill.state, null];
        const action = button(skill.state === "up_to_date" ? "Up to date" : skill.state === "outdated" ? "Update" : "Install",
          { variant: skill.state === "up_to_date" ? null : "primary", size: "sm", disabled: skill.state === "unavailable" || skill.state === "up_to_date" });
        action.addEventListener("click", () => busy(action, async () => {
          const install = (force) => api.post(`agents/skills/${encodeURIComponent(skill.id)}`, { force });
          try {
            const result = await install(false);
            toast(result.message || `${skill.display_name} skill installed.`);
          } catch (error) {
            if (error.status !== 409) { toast(error.message, "error"); return; }
            const replace = await confirmDialog({
              title: `Replace ${skill.display_name}'s skill?`,
              message: "The installed copy differs from the one this build carries, perhaps because someone edited it. Replacing it discards those edits.",
              confirmLabel: "Replace",
              danger: true,
            });
            if (!replace) return;
            try {
              const result = await api.post(`agents/skills/${encodeURIComponent(skill.id)}`, { force: true });
              toast(result.message || "Skill replaced.");
            } catch (e) {
              toast(e.message, "error");
              return;
            }
          }
          draw(await api.get("agents"));
        }));
        return h("div", { class: "list-item", role: "listitem" },
          h("div", { class: "list-main" },
            h("div", { class: "row" }, h("strong", { text: skill.display_name }), badge(label, tone)),
            h("div", { class: "small muted", text: skill.note }),
            skill.destination ? h("div", { class: "small mono truncate", title: skill.destination, text: skill.destination }) : null),
          action);
      });

      const harnesses = data.harnesses.map((harness) => h("div", { class: "card stat" },
        h("div", { class: "row-between" }, h("strong", { text: harness.display_name }), harness.ask_command ? badge("Can wait for answers", "info") : null),
        h("p", { class: "small muted", text: harness.note }),
        codeLine(harness.command),
        harness.ask_command ? codeLine(harness.ask_command) : null));

      mount(page, 
        pageHead("Agents", "Connect coding agents to AgentNotify. The skill teaches an agent when to notify you; a harness makes the host itself report and, for some hosts, wait for your approval."),
        card({
          title: "Skill",
          description: "Installs a Markdown skill into each agent's personal skills folder. Restart the agent to load it.",
          actions: button("Refresh", { size: "sm", iconName: "refresh", onClick: async () => draw(await api.get("agents")) }),
          body: h("div", { class: "list", role: "list" }, skills),
        }),
        card({
          title: "Harnesses",
          description: "Run these in a terminal. A harness edits the host's own configuration, so it stays a deliberate step you take there.",
          body: [
            h("div", { class: "grid-2" }, harnesses),
            notice("With --ask, Codex and Claude Code pause each approval until you answer here, on your phone, or in the terminal. If nobody answers in time they fall back to the normal prompt.", "info"),
          ],
        }));
    };

    draw(await api.get("agents"));
  },
};
