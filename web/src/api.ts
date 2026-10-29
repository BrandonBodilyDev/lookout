import type { Role } from "./types";

const TOKEN_KEY = "lookout.token";
const ROLE_KEY = "lookout.role";

/** Session sign-in for the development token issuer. Storage can be unavailable; then sign-in lasts until reload. */
export const session = {
  get(): { token: string; role: Role } | null {
    try {
      const token = sessionStorage.getItem(TOKEN_KEY);
      const role = sessionStorage.getItem(ROLE_KEY) as Role | null;
      return token && role ? { token, role } : null;
    } catch {
      return null;
    }
  },
  set(token: string, role: Role) {
    try {
      sessionStorage.setItem(TOKEN_KEY, token);
      sessionStorage.setItem(ROLE_KEY, role);
    } catch {
      /* private mode: keep it in memory only */
    }
  },
  clear() {
    try {
      sessionStorage.removeItem(TOKEN_KEY);
      sessionStorage.removeItem(ROLE_KEY);
    } catch {
      /* ignore */
    }
  },
};

export class ApiError extends Error {
  constructor(public status: number, message: string, public current?: unknown) {
    super(message);
  }
}

export async function api<T>(path: string, token: string | null, init: RequestInit = {}): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: {
      ...(init.body ? { "content-type": "application/json" } : {}),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...init.headers,
    },
  });
  if (res.status === 204) return undefined as T;
  const body = res.headers.get("content-type")?.includes("json") ? await res.json() : null;
  if (!res.ok) throw new ApiError(res.status, body?.detail ?? body?.error ?? `HTTP ${res.status}`, body?.current);
  return body as T;
}

export const post = <T>(path: string, token: string | null, body: unknown) =>
  api<T>(path, token, { method: "POST", body: JSON.stringify(body) });

export const put = <T>(path: string, token: string | null, body: unknown) =>
  api<T>(path, token, { method: "PUT", body: JSON.stringify(body) });
