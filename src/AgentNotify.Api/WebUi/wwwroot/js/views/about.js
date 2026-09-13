import { api } from "../api.js";
import { h, mount, clear, pageHead, button, card, notice, icon, codeLine, busy } from "../dom.js";

export default {
  async render(page, ctx) {
    const o = await ctx.refreshOverview() || await api.get("overview");
    const prerelease = String(o.version).includes("-");
    const signOut = button("Sign out", { iconName: "logout" });
    signOut.addEventListener("click", () => busy(signOut, async () => {
      await api.del("session").catch(() => {});
      location.hash = "#/signin";
    }));

    const link = (href, label) => h("a", { href, target: "_blank", rel: "noopener noreferrer", class: "row" }, icon("external"), label);

    mount(page, 
      pageHead("About", "AgentNotify is the local human-attention broker for coding agents.", signOut),
      card({
        body: [
          h("div", { class: "row" }, h("img", { src: "favicon.svg", alt: "", width: 44, height: 44 }),
            h("div", null, h("h2", { class: "page-title", text: "AgentNotify" }), h("p", { class: "muted mono", text: `Version ${o.version}` }))),
          prerelease ? notice("This is a prerelease build. Expect incomplete features and breaking changes.", "warn") : null,
          h("dl", { class: "kv" },
            h("dt", { text: "Data folder" }), h("dd", { class: "mono small", text: o.data_directory }),
            h("dt", { text: "Local API" }), h("dd", { class: "mono small", text: o.api_url }),
            h("dt", { text: "Licence" }), h("dd", { text: "MIT. Provided as is, without warranty of any kind." }),
            h("dt", { text: "Publisher" }), h("dd", { text: "Kabani Tech Private Limited. Author Akash P." })),
        ],
      }),
      h("div", { class: "grid-2" },
        card({
          title: "Local first",
          body: [
            h("p", { class: "muted", text: "History, questions, and channel credentials live on this computer. This page is served by the broker itself, only to this machine, and never receives a stored credential back." }),
            h("p", { class: "muted", text: "Open it again at any time with:" }),
            codeLine("agentnotify ui"),
          ],
        }),
        card({
          title: "Links",
          body: h("div", { class: "stack" },
            link("https://github.com/Akash97p/agent-notify", "Source code"),
            link("https://akash97p.github.io/agent-notify/docs/", "Documentation"),
            link("https://github.com/Akash97p/agent-notify/releases", "Releases")),
        })));
  },
};
