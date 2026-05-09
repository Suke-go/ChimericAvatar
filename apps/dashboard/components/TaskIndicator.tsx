"use client";

import { useState } from "react";

import { useElapsedSeconds, useTaskTracker } from "../lib/useTaskTracker";

export default function TaskIndicator() {
  const { tasks } = useTaskTracker();
  const [open, setOpen] = useState(false);
  const oldest = tasks[0];
  const elapsed = useElapsedSeconds(oldest?.startedAt);

  if (tasks.length === 0) return null;

  return (
    <div style={{ position: "relative" }}>
      <button
        type="button"
        className="badge amber"
        onClick={() => setOpen((v) => !v)}
        style={{
          display: "inline-flex",
          alignItems: "center",
          gap: 6,
          cursor: "pointer",
          background: "rgba(210,160,79,0.10)",
          border: "1px solid rgba(210,160,79,0.5)",
          color: "var(--amber)",
          fontFamily: "var(--font-mono)",
          padding: "2px 8px",
        }}
      >
        <span className="spinner" />
        {tasks.length === 1
          ? `${oldest.label} · ${elapsed}s`
          : `${tasks.length} tasks · ${elapsed}s`}
      </button>
      {open && tasks.length > 1 && (
        <ul
          className="card"
          style={{
            position: "absolute",
            top: "calc(100% + 6px)",
            right: 0,
            width: 280,
            zIndex: 50,
            listStyle: "none",
            padding: 8,
            margin: 0,
          }}
        >
          {tasks.map((task) => (
            <TaskRow key={task.id} label={task.label} startedAt={task.startedAt} />
          ))}
        </ul>
      )}
    </div>
  );
}

function TaskRow({ label, startedAt }: { label: string; startedAt: number }) {
  const elapsed = useElapsedSeconds(startedAt);
  return (
    <li
      style={{
        display: "flex",
        justifyContent: "space-between",
        gap: 8,
        padding: "4px 6px",
        fontFamily: "var(--font-mono)",
        fontSize: 11,
      }}
    >
      <span>{label}</span>
      <span className="muted">{elapsed}s</span>
    </li>
  );
}
