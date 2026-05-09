"use client";

import { CurrentUser, UserRole } from "./types";
import { getSupabaseClient, supabaseEnabled } from "./supabase-client";

const USER_KEY = "chimera-user";
const ROLE_KEY = "chimera-role";

export type AuthHeaders = {
  Authorization?: string;
  "X-Debug-User"?: string;
  "X-Debug-Role"?: string;
};

export type SessionAuth = {
  mode: "supabase" | "debug";
  user: CurrentUser;
  accessToken?: string;
};

export function saveDebugAuth(userId: string, role: UserRole): void {
  if (typeof window === "undefined") return;
  window.localStorage.setItem(USER_KEY, userId);
  window.localStorage.setItem(ROLE_KEY, role);
}

export function clearDebugAuth(): void {
  if (typeof window === "undefined") return;
  window.localStorage.removeItem(USER_KEY);
  window.localStorage.removeItem(ROLE_KEY);
}

function readDebugAuth(): CurrentUser | null {
  if (typeof window === "undefined") return null;
  const userId = window.localStorage.getItem(USER_KEY);
  const role = window.localStorage.getItem(ROLE_KEY) as UserRole | null;
  if (!userId || !role) return null;
  return { user_id: userId, role };
}

export async function readAuth(): Promise<SessionAuth | null> {
  if (supabaseEnabled) {
    const client = getSupabaseClient();
    if (!client) return null;
    const { data, error } = await client.auth.getSession();
    if (error || !data.session) return null;
    const session = data.session;
    const role = (session.user.app_metadata?.app_role as UserRole | undefined) ?? "member";
    return {
      mode: "supabase",
      user: { user_id: session.user.id, role },
      accessToken: session.access_token,
    };
  }
  const debug = readDebugAuth();
  if (!debug) return null;
  return { mode: "debug", user: debug };
}

export async function authHeaders(): Promise<AuthHeaders> {
  const session = await readAuth();
  if (!session) return {};
  if (session.mode === "supabase" && session.accessToken) {
    return { Authorization: `Bearer ${session.accessToken}` };
  }
  return {
    "X-Debug-User": session.user.user_id,
    "X-Debug-Role": session.user.role,
  };
}

export async function signOut(): Promise<void> {
  if (supabaseEnabled) {
    const client = getSupabaseClient();
    await client?.auth.signOut();
  }
  clearDebugAuth();
}
