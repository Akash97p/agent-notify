// The web UI's only channel to the broker. Every state-changing call carries the header the broker
// requires, which a page on another site cannot send.

const BASE = "api";

export class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

export async function request(method, path, body, { raw = false } = {}) {
  const headers = { Accept: "application/json" };
  const init = { method, headers, credentials: "same-origin", cache: "no-store" };
  if (method !== "GET") headers["X-AgentNotify-UI"] = "1";
  if (body instanceof FormData) {
    init.body = body;
  } else if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }

  let response;
  try {
    response = await fetch(`${BASE}/${path}`, init);
  } catch {
    throw new ApiError("The AgentNotify broker is not responding. Is it still running?", 0);
  }

  if (raw) return response;
  const text = await response.text();
  let data = null;
  if (text) {
    try { data = JSON.parse(text); } catch { data = null; }
  }
  if (!response.ok) {
    throw new ApiError((data && data.error) || `Request failed (${response.status}).`, response.status);
  }
  return data;
}

export const api = {
  get: (path) => request("GET", path),
  post: (path, body) => request("POST", path, body ?? {}),
  put: (path, body) => request("PUT", path, body),
  del: (path) => request("DELETE", path),
};
