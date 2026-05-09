"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { ReactNode, useEffect, useState } from "react";

import { signOut } from "../lib/auth-store";
import { TaskTrackerProvider, useTaskTracker } from "../lib/useTaskTracker";
import TaskIndicator from "./TaskIndicator";

const SYSTEM_LABEL = "CHIMERA::PRESENTER";

export default function AppShell({ children }: { children: ReactNode }) {
  return (
    <TaskTrackerProvider>
      <AppShellInner>{children}</AppShellInner>
    </TaskTrackerProvider>
  );
}

function AppShellInner({ children }: { children: ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const [mounted, setMounted] = useState(false);
  const [open, setOpen] = useState(false);
  const displayPathname = mounted ? (pathname ?? "") : "";

  useEffect(() => {
    setMounted(true);
  }, []);

  useEffect(() => {
    setOpen(false);
  }, [pathname]);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const segments = displayPathname.split("/").filter(Boolean);
  const { tasks } = useTaskTracker();

  return (
    <div className="app-shell">
      <header className={`system-bar ${tasks.length > 0 ? "has-tasks" : ""}`}>
        <button
          aria-label="Open menu"
          className="menu-toggle"
          onClick={() => setOpen((v) => !v)}
          type="button"
        >
          <svg width="14" height="14" viewBox="0 0 14 14" fill="none">
            <path d="M2 3h10M2 7h10M2 11h10" stroke="currentColor" strokeWidth="1.4" strokeLinecap="square" />
          </svg>
        </button>
        <span className="brand">{SYSTEM_LABEL}</span>
        <span className="crumbs">
          {segments.length === 0 ? (
            <span className="here">/</span>
          ) : (
            segments.map((seg, idx) => (
              <span key={`${seg}-${idx}`}>
                <span className="sep">/</span>{" "}
                <span className={idx === segments.length - 1 ? "here" : ""}>{seg}</span>
              </span>
            ))
          )}
        </span>
        <span className="status">
          <TaskIndicator />
          <span className="status-dot" />
          ONLINE
        </span>
      </header>

      <div
        aria-hidden={!open}
        className={`menu-backdrop ${open ? "open" : ""}`}
        onClick={() => setOpen(false)}
      />
      <aside className={`side-menu ${open ? "open" : ""}`}>
        <div className="group">
          <div className="group-title">Workspace</div>
          <nav>
            <Link className={displayPathname === "/sessions" ? "active" : ""} href="/sessions">
              ▸ Sessions
            </Link>
          </nav>
        </div>
        <div className="group">
          <div className="group-title">Account</div>
          <nav>
            <button
              type="button"
              onClick={async () => {
                await signOut();
                router.push("/login");
              }}
            >
              ▸ Sign out
            </button>
          </nav>
        </div>
        <div className="group">
          <div className="group-title">System</div>
          <p className="hint" style={{ margin: 0 }}>
            <span className="status-dot" style={{ display: "inline-block", marginRight: 6 }} />
            link uplink stable
          </p>
        </div>
      </aside>

      <main>{children}</main>
    </div>
  );
}
