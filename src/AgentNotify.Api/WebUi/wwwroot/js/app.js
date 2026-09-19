import { api } from "./api.js";
import { h, mount, icon, clear, button, brandMark, notice, toast } from "./dom.js";
import overview from "./views/overview.js";
import attention from "./views/attention.js";
import questions from "./views/questions.js";
import channels from "./views/channels.js";
import routes from "./views/routes.js";
import notifications from "./views/notifications.js";
import sounds from "./views/sounds.js";
import agents from "./views/agents.js";
import about from "./views/about.js";
import usage from "./views/usage.js";
import quota from "./views/quota.js";
import routerProviders from "./views/router-providers.js";
import routerRouting from "./views/router-routing.js";
import routerAgents from "./views/router-agents.js";
import routerActivity from "./views/router-activity.js";
import routerSettings from "./views/router-settings.js";
import routerEffort from "./views/router-effort.js";
import insights from "./views/insights.js";

// Navigation is grouped so new areas slot in as another group or entry without reshaping the shell.
const NAV = [
  { label: "Activity", items: [
    { path: "overview", title: "Overview", icon: "home", view: overview },
    { path: "attention", title: "Attention", icon: "bell", view: attention, count: "active_notifications" },
    { path: "questions", title: "Questions", icon: "question", view: questions, count: "pending_questions" },
  ] },
  { label: "Delivery", items: [
    { path: "channels", title: "Channels", icon: "send", view: channels },
    { path: "routes", title: "Routes", icon: "route", view: routes },
  ] },
  { label: "Configuration", items: [
    { path: "notifications", title: "Notifications", icon: "sliders", view: notifications },
    { path: "sounds", title: "Sounds", icon: "volume", view: sounds },
    { path: "agents", title: "Agents", icon: "bot", view: agents },
  ] },
  { label: "Model router", items: [
    { path: "router", title: "Providers", icon: "server", view: routerProviders },
    { path: "router-routing", title: "Routing", icon: "shuffle", view: routerRouting },
    { path: "router-agents", title: "Agents", icon: "bot", view: routerAgents },
    { path: "router-activity", title: "Activity", icon: "pulse", view: routerActivity },
    { path: "router-settings", title: "Settings", icon: "sliders", view: routerSettings },
    { path: "router-effort", title: "Effort mapping", icon: "gauge", view: routerEffort },
  ] },
  { label: "Insights", items: [
    { path: "insights", title: "Dashboard", icon: "chart", view: insights },
    { path: "usage", title: "Usage", icon: "pulse", view: usage },
    { path: "quota", title: "Live quota", icon: "gauge", view: quota },
  ] },
  { label: "System", items: [
    { path: "about", title: "About", icon: "info", view: about },
  ] },
];

const ROUTES = new Map(NAV.flatMap((group) => group.items).map((item) => [item.path, item]));

const state = {
  overview: null,
  cleanup: null,
  root: document.getElementById("app"),
  navLinks: new Map(),
  countBadges: new Map(),
  statusDot: null,
  statusText: null,
  shell: null,
  main: null,
  renderToken: 0,
};

// ---- theme ---------------------------------------------------------------------------------

function storedTheme() {
  try { return localStorage.getItem("agentnotify-theme"); } catch { return null; }
}

function applyTheme(theme) {
  if (theme === "light" || theme === "dark") document.documentElement.dataset.theme = theme;
  else delete document.documentElement.dataset.theme;
}

function currentTheme() {
  return document.documentElement.dataset.theme
    || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
}

applyTheme(storedTheme());

// ---- shell ---------------------------------------------------------------------------------

function buildShell() {
  const nav = h("nav", { "aria-label": "Main" });
  for (const group of NAV) {
    const list = h("div", { class: "nav-group" }, h("div", { class: "nav-label", text: group.label }));
    for (const item of group.items) {
      const count = item.count ? h("span", { class: "nav-count", hidden: true }) : null;
      const link = h("a", { class: "nav-link", href: `#/${item.path}`, onClick: closeNav }, icon(item.icon), h("span", { text: item.title }), count);
      state.navLinks.set(item.path, link);
      if (count) state.countBadges.set(item.count, count);
      list.append(link);
    }
    nav.append(list);
  }

  state.statusDot = h("span", { class: "dot" });
  state.statusText = h("span", { text: "Connected" });
  const themeButton = button("", { variant: "ghost", size: "sm", iconName: currentTheme() === "dark" ? "sun" : "moon", title: "Switch theme" });
  themeButton.addEventListener("click", () => {
    const next = currentTheme() === "dark" ? "light" : "dark";
    applyTheme(next);
    try { localStorage.setItem("agentnotify-theme", next); } catch { /* per-viewer convenience only */ }
    themeButton.replaceChildren(icon(next === "dark" ? "sun" : "moon"));
  });

  const sidebar = h("aside", { class: "sidebar" },
    h("a", { class: "brand", href: "#/overview", onClick: closeNav }, brandMark(),
      h("span", null, h("span", { class: "brand-name", text: "AgentNotify" }), h("br"), h("span", { class: "brand-sub", text: "Local control" }))),
    nav,
    h("div", { class: "sidebar-foot" },
      h("div", { class: "row-between" }, h("span", { class: "status-line" }, state.statusDot, state.statusText), themeButton)));

  state.main = h("main", { class: "main", id: "main", tabindex: "-1" });
  const topbar = h("div", { class: "topbar" },
    button("", { variant: "ghost", iconName: "menu", title: "Open navigation", onClick: () => state.shell.classList.add("nav-open") }),
    brandMark(), h("strong", { text: "AgentNotify" }));

  state.shell = h("div", { class: "shell" }, sidebar, h("div", { class: "grow" }, topbar, state.main));
  const scrim = h("div", { class: "scrim", onClick: closeNav });
  const observer = new MutationObserver(() => {
    if (state.shell.classList.contains("nav-open")) state.shell.append(scrim); else scrim.remove();
  });
  observer.observe(state.shell, { attributes: true, attributeFilter: ["class"] });

  clear(state.root);
  state.root.className = "";
  state.root.removeAttribute("aria-busy");
  state.root.append(state.shell);
}

function closeNav() {
  state.shell?.classList.remove("nav-open");
}

// ---- data shared by the shell --------------------------------------------------------------

export async function refreshOverview() {
  try {
    state.overview = await api.get("overview");
    setConnected(true);
    for (const [key, badgeEl] of state.countBadges) {
      const value = state.overview.counts[key] || 0;
      badgeEl.textContent = String(value);
      badgeEl.hidden = value === 0;
    }
    return state.overview;
  } catch (error) {
    setConnected(false);
    return state.overview;
  }
}

function setConnected(connected) {
  if (!state.statusDot) return;
  state.statusDot.classList.toggle("off", !connected);
  state.statusText.textContent = connected ? "Broker connected" : "Broker unreachable";
}

// ---- routing -------------------------------------------------------------------------------

function currentPath() {
  const hash = location.hash.replace(/^#\/?/, "");
  const [path, query] = hash.split("?");
  return { path: path || "overview", params: new URLSearchParams(query || "") };
}

async function render() {
  const { path, params } = currentPath();
  if (!state.shell) {
    buildShell();
    refreshOverview();
  }

  const route = ROUTES.get(path) || ROUTES.get("overview");
  for (const [key, link] of state.navLinks) {
    if (key === route.path) link.setAttribute("aria-current", "page"); else link.removeAttribute("aria-current");
  }
  document.title = `${route.title} · AgentNotify`;

  if (state.cleanup) { try { state.cleanup(); } catch { /* view already gone */ } state.cleanup = null; }
  const token = ++state.renderToken;
  clear(state.main);
  const page = h("div", { class: "page" }, h("div", { class: "skeleton" }));
  state.main.append(page);

  const ctx = {
    params,
    overview: () => state.overview,
    refreshOverview,
    isCurrent: () => token === state.renderToken,
    navigate: (to) => { location.hash = `#/${to}`; },
  };
  try {
    const cleanup = await route.view.render(page, ctx);
    if (token === state.renderToken) state.cleanup = typeof cleanup === "function" ? cleanup : null;
    else if (typeof cleanup === "function") cleanup();
  } catch (error) {
    if (token !== state.renderToken) return;
    mount(page, notice(error.message || "Something went wrong loading this page.", "danger"));
  }
}

// ---- start ---------------------------------------------------------------------------------

async function start() {
  await refreshOverview();
  if (!state.overview) {
    mount(state.root, h("div", { class: "boot" }, notice("The AgentNotify broker is not responding. Is it still running?", "danger")));
    state.root.removeAttribute("aria-busy");
  } else {
    await render();
  }

  window.addEventListener("hashchange", render);

  // Keeps the navigation counts and the connection indicator honest while the page is open.
  setInterval(() => {
    if (document.visibilityState === "visible" && state.shell) refreshOverview();
  }, 10000);
}

window.addEventListener("unhandledrejection", (event) => {
  toast(event.reason?.message || "Unexpected error.", "error");
});

start();
