"use client";

import {
  ReactNode,
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from "react";

export type TrackedTask = { id: string; label: string; startedAt: number };

type Ctx = {
  tasks: TrackedTask[];
  start: (label: string) => string;
  finish: (id: string) => void;
  /** Convenience: wrap a promise so it auto-registers and clears on settle. */
  track: <T>(label: string, fn: () => Promise<T>) => Promise<T>;
};

const TaskTrackerContext = createContext<Ctx | null>(null);

function newId() {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) {
    return crypto.randomUUID();
  }
  return `task-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

export function TaskTrackerProvider({ children }: { children: ReactNode }) {
  const [tasks, setTasks] = useState<TrackedTask[]>([]);

  const start = useCallback((label: string) => {
    const id = newId();
    setTasks((prev) => [...prev, { id, label, startedAt: Date.now() }]);
    return id;
  }, []);

  const finish = useCallback((id: string) => {
    setTasks((prev) => prev.filter((task) => task.id !== id));
  }, []);

  const track = useCallback(
    async <T,>(label: string, fn: () => Promise<T>): Promise<T> => {
      const id = start(label);
      try {
        return await fn();
      } finally {
        finish(id);
      }
    },
    [start, finish],
  );

  const value = useMemo(() => ({ tasks, start, finish, track }), [tasks, start, finish, track]);
  return <TaskTrackerContext.Provider value={value}>{children}</TaskTrackerContext.Provider>;
}

export function useTaskTracker(): Ctx {
  const ctx = useContext(TaskTrackerContext);
  if (!ctx) {
    // Safe no-op fallback so components used outside the provider don't crash
    return {
      tasks: [],
      start: () => "",
      finish: () => undefined,
      track: async <T,>(_label: string, fn: () => Promise<T>) => fn(),
    };
  }
  return ctx;
}

/** Live elapsed time (in seconds) for a task. */
export function useElapsedSeconds(startedAt: number | null | undefined): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!startedAt) return;
    const t = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(t);
  }, [startedAt]);
  if (!startedAt) return 0;
  return Math.max(0, Math.floor((now - startedAt) / 1000));
}
