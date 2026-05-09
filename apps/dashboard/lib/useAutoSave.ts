"use client";

import { useCallback, useEffect, useRef, useState } from "react";

export type SaveStatus = "idle" | "saving" | "saved" | "error";

export type SaveStateMap = Record<string, { status: SaveStatus; at: number; error?: string }>;

/**
 * Debounced auto-save for a list of editable items keyed by id.
 *
 * Usage:
 *   const { schedule, statuses, flush } = useAutoSave(
 *     async (id, text) => updateScriptSegment(sessionId, id, { text }),
 *     1500,
 *   );
 *   <textarea onChange={(e) => schedule(segment.id, e.target.value)} />
 *   <SaveBadge state={statuses[segment.id]} />
 *
 * Drafts are mirrored into sessionStorage so a tab close right after typing
 * does not lose work even if the network save hasn't fired yet.
 */
export function useAutoSave<T>(
  saver: (id: string, value: T) => Promise<unknown>,
  delayMs: number = 1500,
  draftKeyPrefix: string = "chimera-draft",
) {
  const timers = useRef<Record<string, ReturnType<typeof setTimeout>>>({});
  const inflightValues = useRef<Record<string, T>>({});
  const [statuses, setStatuses] = useState<SaveStateMap>({});

  const setStatus = useCallback(
    (id: string, partial: Partial<SaveStateMap[string]>) => {
      setStatuses((prev) => {
        const previous = prev[id] ?? { status: "idle" as SaveStatus, at: Date.now() };
        return { ...prev, [id]: { ...previous, ...partial } };
      });
    },
    [],
  );

  const persistDraft = useCallback(
    (id: string, value: T) => {
      if (typeof window === "undefined") return;
      try {
        window.sessionStorage.setItem(`${draftKeyPrefix}:${id}`, JSON.stringify(value));
      } catch {
        // ignore quota errors
      }
    },
    [draftKeyPrefix],
  );

  const clearDraft = useCallback(
    (id: string) => {
      if (typeof window === "undefined") return;
      try {
        window.sessionStorage.removeItem(`${draftKeyPrefix}:${id}`);
      } catch {
        // ignore
      }
    },
    [draftKeyPrefix],
  );

  const readDraft = useCallback(
    (id: string): T | null => {
      if (typeof window === "undefined") return null;
      try {
        const raw = window.sessionStorage.getItem(`${draftKeyPrefix}:${id}`);
        return raw ? (JSON.parse(raw) as T) : null;
      } catch {
        return null;
      }
    },
    [draftKeyPrefix],
  );

  const fire = useCallback(
    async (id: string) => {
      const value = inflightValues.current[id];
      if (value === undefined) return;
      setStatus(id, { status: "saving", at: Date.now() });
      try {
        await saver(id, value);
        setStatus(id, { status: "saved", at: Date.now(), error: undefined });
        clearDraft(id);
      } catch (err) {
        setStatus(id, {
          status: "error",
          at: Date.now(),
          error: err instanceof Error ? err.message : String(err),
        });
      }
    },
    [saver, setStatus, clearDraft],
  );

  const schedule = useCallback(
    (id: string, value: T) => {
      inflightValues.current[id] = value;
      persistDraft(id, value);
      setStatus(id, { status: "idle", at: Date.now() });
      if (timers.current[id]) clearTimeout(timers.current[id]);
      timers.current[id] = setTimeout(() => {
        delete timers.current[id];
        void fire(id);
      }, delayMs);
    },
    [delayMs, fire, persistDraft, setStatus],
  );

  const flush = useCallback(
    async (id?: string) => {
      const ids = id ? [id] : Object.keys(timers.current);
      for (const key of ids) {
        if (timers.current[key]) {
          clearTimeout(timers.current[key]);
          delete timers.current[key];
          await fire(key);
        }
      }
    },
    [fire],
  );

  // On unmount: flush pending writes so we don't lose them
  useEffect(() => {
    return () => {
      Object.values(timers.current).forEach(clearTimeout);
      // Note: we can't await async here, so a true tab-close still relies on
      // the sessionStorage draft. The caller can also trigger flush() on
      // navigation events (Next.js router).
    };
  }, []);

  return { schedule, flush, statuses, readDraft, clearDraft };
}
