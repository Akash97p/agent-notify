import { renderRouter } from "./router-shared.js";

export default {
  async render(page, ctx) {
    await renderRouter(page, ctx, "routing");
  },
};
