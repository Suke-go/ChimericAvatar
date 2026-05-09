"use client";

import { ReactNode, useEffect, useState } from "react";

import { fetchSessions } from "../lib/api";
import { SessionItem } from "../lib/types";
import SessionTabs from "./SessionTabs";

export default function SessionWorkspace({
  sessionId,
  eyebrow,
  title,
  actions,
  children,
}: {
  sessionId: string;
  eyebrow?: string;
  title?: string;
  actions?: ReactNode;
  children: ReactNode;
}) {
  const [session, setSession] = useState<SessionItem | null>(null);

  useEffect(() => {
    let cancelled = false;
    fetchSessions()
      .then((list) => {
        if (cancelled) return;
        const found = list.find((s) => s.id === sessionId) ?? null;
        setSession(found);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
    };
  }, [sessionId]);

  return (
    <div className="workspace">
      <header className="page-head">
        <div className="lockup">
          <span className="eyebrow">
            {eyebrow ?? "Session"} {session?.session_code ? `// ${session.session_code}` : ""}
          </span>
          <h1>{title ?? session?.title ?? "—"}</h1>
        </div>
        {actions}
      </header>
      <SessionTabs sessionId={sessionId} />
      {children}
    </div>
  );
}
