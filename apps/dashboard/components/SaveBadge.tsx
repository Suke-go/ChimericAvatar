"use client";

import { SaveStatus } from "../lib/useAutoSave";

const LABEL: Record<SaveStatus, string> = {
  idle: "draft",
  saving: "saving…",
  saved: "saved",
  error: "save failed",
};

const VARIANT: Record<SaveStatus, "muted" | "amber" | "" | "coral"> = {
  idle: "amber",
  saving: "amber",
  saved: "",
  error: "coral",
};

function relativeAge(at: number): string {
  if (!at) return "";
  const sec = Math.max(0, Math.floor((Date.now() - at) / 1000));
  if (sec < 5) return "now";
  if (sec < 60) return `${sec}s ago`;
  return `${Math.floor(sec / 60)}m ago`;
}

export default function SaveBadge({
  state,
}: {
  state?: { status: SaveStatus; at: number; error?: string };
}) {
  if (!state) return <span className="badge muted">unedited</span>;
  return (
    <span
      className={`badge ${VARIANT[state.status]}`}
      title={state.error ?? ""}
    >
      {LABEL[state.status]}
      {state.status === "saved" ? ` · ${relativeAge(state.at)}` : ""}
    </span>
  );
}
