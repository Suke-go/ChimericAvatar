"use client";

import { createBrowserClient } from "@supabase/ssr";

const url = process.env.NEXT_PUBLIC_SUPABASE_URL ?? "";
// New (Nov 2025+) Supabase projects expose a sb_publishable_... key.
// Legacy projects still use the JWT-based anon key. Either is accepted
// by @supabase/supabase-js as the second argument to createBrowserClient.
const publishableKey = process.env.NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY ?? "";
const anonKey = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY ?? "";
const apiKey = publishableKey || anonKey;

export const supabaseEnabled = Boolean(url && apiKey);

let _client: ReturnType<typeof createBrowserClient> | null = null;

export function getSupabaseClient() {
  if (!supabaseEnabled) return null;
  if (_client) return _client;
  _client = createBrowserClient(url, apiKey);
  return _client;
}
