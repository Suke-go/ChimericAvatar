"use client";

/**
 * Cache short-lived signed asset URLs in sessionStorage so that page reloads
 * within the URL's TTL don't have to:
 *   1) re-mint the URL via /image-url, and
 *   2) lose the browser's HTTP cache for the actual asset (a fresh signed URL
 *      is a different cache key).
 *
 * Entries auto-expire 60s before their declared expires_at so we never serve
 * a URL that's about to die.
 */

type Entry = { url: string; mime?: string | null; expires_at: number };
const SAFETY_MARGIN_SEC = 60;

function read(key: string): Entry | null {
  if (typeof window === "undefined") return null;
  try {
    const raw = window.sessionStorage.getItem(key);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Entry;
    if (typeof parsed?.expires_at !== "number") return null;
    if (Date.now() / 1000 + SAFETY_MARGIN_SEC > parsed.expires_at) {
      window.sessionStorage.removeItem(key);
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
}

function write(key: string, entry: Entry): void {
  if (typeof window === "undefined") return;
  try {
    window.sessionStorage.setItem(key, JSON.stringify(entry));
  } catch {
    // ignore quota errors
  }
}

export async function cachedSignedUrl<
  T extends {
    url: string | null;
    expires_at: number;
    mime_type?: string | null;
    missing?: boolean;
  },
>(key: string, fetcher: () => Promise<T>): Promise<T> {
  const cached = read(key);
  if (cached) {
    return { url: cached.url, expires_at: cached.expires_at, mime_type: cached.mime ?? null } as T;
  }
  const fresh = await fetcher();
  // Don't cache a missing-asset answer. Re-uploads recreate the storage
  // object and should be visible immediately, not 30 minutes later.
  if (fresh.url && !fresh.missing) {
    write(key, { url: fresh.url, mime: fresh.mime_type ?? null, expires_at: fresh.expires_at });
  }
  return fresh;
}

export function invalidateSignedUrl(key: string): void {
  if (typeof window === "undefined") return;
  window.sessionStorage.removeItem(key);
}
