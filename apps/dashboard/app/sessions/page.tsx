"use client";

import Link from "next/link";
import { FormEvent, useEffect, useState } from "react";

import { createRuntimeEntry, createSession, fetchMe, fetchSessions, getRuntimeManifestUrl, publishSession } from "../../lib/api";
import { CurrentUser, RuntimeEntry, SessionItem } from "../../lib/types";
import { useTaskTracker } from "../../lib/useTaskTracker";

export default function SessionsPage() {
  const [sessions, setSessions] = useState<SessionItem[]>([]);
  const [currentUser, setCurrentUser] = useState<CurrentUser | null>(null);
  const [title, setTitle] = useState("");
  const [abstract, setAbstract] = useState("");
  const [eventName, setEventName] = useState("");
  const [presenterName, setPresenterName] = useState("");
  const [presenterNameKana, setPresenterNameKana] = useState("");
  const [presenterAffiliation, setPresenterAffiliation] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [publishMessage, setPublishMessage] = useState<string | null>(null);
  const tracker = useTaskTracker();
  const [publishingId, setPublishingId] = useState<string | null>(null);
  const [runtimeEntryId, setRuntimeEntryId] = useState<string | null>(null);
  const [runtimeEntries, setRuntimeEntries] = useState<Record<string, RuntimeEntry>>({});

  async function load() {
    try {
      const [me, items] = await Promise.all([fetchMe(), fetchSessions()]);
      setCurrentUser(me);
      setSessions(items);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load");
    }
  }

  useEffect(() => {
    load();
  }, []);

  async function handleCreate(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await createSession({
      title,
      abstract,
      event_name: eventName,
      presenter_name: presenterName || undefined,
      presenter_name_kana: presenterNameKana || undefined,
      presenter_affiliation: presenterAffiliation || undefined,
    });
    setTitle("");
    setAbstract("");
    setEventName("");
    setPresenterName("");
    setPresenterNameKana("");
    setPresenterAffiliation("");
    await load();
  }

  async function handlePublish(sessionId: string) {
    setPublishingId(sessionId);
    try {
      const updated = await tracker.track("Publishing session…", () => publishSession(sessionId));
      setPublishMessage(`Published ${updated.session_code}`);
      await load();
    } catch (err) {
      // The API returns a structured 409 with `code: "manifest_assets_missing"`
      // when storage objects are gone. Offer a one-click force re-publish so
      // the operator can ship a placeholder if they know what they're doing.
      const message = err instanceof Error ? err.message : "Failed to publish";
      if (message.includes("manifest_assets_missing")) {
        const proceed = window.confirm(
          "Some storage objects referenced by this session are missing.\n\n" +
            "Publishing now will produce a manifest with `missing: true` placeholders. " +
            "Re-upload the missing files first if you can; otherwise force-publish to proceed.",
        );
        if (proceed) {
          try {
            const updated = await tracker.track("Force-publishing session…", () =>
              publishSession(sessionId, { force: true }),
            );
            setPublishMessage(`Force-published ${updated.session_code} with missing assets`);
            await load();
            return;
          } catch (forceErr) {
            setPublishMessage(forceErr instanceof Error ? forceErr.message : "Force-publish failed");
            return;
          } finally {
            setPublishingId(null);
          }
        }
      }
      setPublishMessage(message);
    } finally {
      setPublishingId(null);
    }
  }

  async function handleRuntimeEntry(sessionId: string) {
    setRuntimeEntryId(sessionId);
    try {
      const entry = await tracker.track("Creating runtime entry...", () => createRuntimeEntry(sessionId));
      setRuntimeEntries((prev) => ({ ...prev, [sessionId]: entry }));
      let message = `Runtime entry created for ${entry.session_code}`;
      if (navigator.clipboard) {
        try {
          await navigator.clipboard.writeText(entry.join_uri);
          message = `Runtime entry copied for ${entry.session_code}`;
        } catch {
          // Clipboard can be blocked on local/non-secure origins; the URI is still visible.
        }
      }
      setPublishMessage(message);
    } catch (err) {
      setPublishMessage(err instanceof Error ? err.message : "Failed to create runtime entry");
    } finally {
      setRuntimeEntryId(null);
    }
  }

  return (
    <div className="workspace">
      <header className="page-head">
        <div className="lockup">
          <span className="eyebrow">
            {currentUser ? `${currentUser.role} ▸ ${currentUser.user_id.slice(0, 8)}` : "—"}
          </span>
          <h1>Sessions</h1>
        </div>
        <span className="badge muted">{sessions.length} TOTAL</span>
      </header>

      {error ? <div className="alert error">{error}</div> : null}
      {publishMessage ? <div className="alert info">{publishMessage}</div> : null}

      <div className="grid split-3-1">
        <section className="panel">
          <div className="panel-title">
            <span className="marker" />
            REGISTRY
            <span className="panel-subtitle">// active sessions</span>
          </div>
          {sessions.length === 0 ? (
            <p className="hint">No sessions yet. Create one on the right →</p>
          ) : (
            <div className="list">
              {sessions.map((session) => (
                <div className="card" key={session.id}>
                  <div className="row" style={{ justifyContent: "space-between" }}>
                    <div>
                      <h3 style={{ margin: 0 }}>{session.title}</h3>
                      <p className="hint" style={{ margin: "2px 0 0" }}>
                        {session.event_name || "—"}
                      </p>
                    </div>
                    <div className="row" style={{ gap: 6 }}>
                      <span className="badge muted">{session.session_code}</span>
                      <span
                        className={`badge ${
                          session.status === "published" ? "" : session.status === "draft" ? "amber" : "muted"
                        }`}
                      >
                        {session.status}
                      </span>
                    </div>
                  </div>
                  <div className="row" style={{ marginTop: 12, flexWrap: "wrap" }}>
                    <Link className="button secondary" href={`/sessions/${session.id}/poster`}>
                      Poster
                    </Link>
                    <Link className="button secondary" href={`/sessions/${session.id}/knowledge`}>
                      Knowledge
                    </Link>
                    <Link className="button secondary" href={`/sessions/${session.id}/avatar`}>
                      Avatar
                    </Link>
                    <Link className="button secondary" href={`/sessions/${session.id}/preview`}>
                      Preview
                    </Link>
                    <button
                      className="button"
                      type="button"
                      disabled={publishingId === session.id}
                      onClick={() => handlePublish(session.id)}
                    >
                      {publishingId === session.id ? (
                        <>
                          <span className="spinner" /> Publishing…
                        </>
                      ) : (
                        "Publish ↑"
                      )}
                    </button>
                    <button
                      className="button secondary"
                      type="button"
                      disabled={session.status !== "published" || runtimeEntryId === session.id}
                      onClick={() => handleRuntimeEntry(session.id)}
                    >
                      {runtimeEntryId === session.id ? (
                        <>
                          <span className="spinner" /> Creating...
                        </>
                      ) : (
                        "Unity entry"
                      )}
                    </button>
                  </div>
                  {session.status === "published" ? (
                    <>
                      <p
                        className="hint"
                        style={{ marginTop: 10, overflowWrap: "anywhere" }}
                      >
                        manifest:{" "}
                        <code>{getRuntimeManifestUrl(session.session_code)}</code>
                      </p>
                      {runtimeEntries[session.id] ? (
                        <p
                          className="hint"
                          style={{ marginTop: 8, overflowWrap: "anywhere" }}
                        >
                          unity entry expires {runtimeEntries[session.id].expires_at}:{" "}
                          <code>{runtimeEntries[session.id].join_uri}</code>
                        </p>
                      ) : null}
                    </>
                  ) : null}
                </div>
              ))}
            </div>
          )}
        </section>

        <aside className="panel">
          <div className="panel-title">
            <span className="marker" />
            NEW
          </div>
          <form className="stack" onSubmit={handleCreate}>
            <div className="field">
              <label htmlFor="title">Title</label>
              <input id="title" value={title} onChange={(e) => setTitle(e.target.value)} required />
            </div>
            <div className="field">
              <label htmlFor="abstract">Abstract</label>
              <textarea id="abstract" value={abstract} onChange={(e) => setAbstract(e.target.value)} rows={4} />
            </div>
            <div className="field">
              <label htmlFor="eventName">Event</label>
              <input id="eventName" value={eventName} onChange={(e) => setEventName(e.target.value)} />
            </div>

            <div className="field">
              <label htmlFor="presenterName">Presenter name (発表者名)</label>
              <input
                id="presenterName"
                value={presenterName}
                onChange={(e) => setPresenterName(e.target.value)}
                placeholder="例: 山田 太郎 / Taro Yamada"
              />
            </div>
            <div className="field">
              <label htmlFor="presenterNameKana">Reading (フリガナ / romaji)</label>
              <input
                id="presenterNameKana"
                value={presenterNameKana}
                onChange={(e) => setPresenterNameKana(e.target.value)}
                placeholder="例: やまだ たろう / Taro Yamada"
              />
              <p className="hint" style={{ marginTop: 4 }}>
                TTSの読み上げで使われます。漢字名はひらがな(カタカナ)、英名はそのままで OK。
              </p>
            </div>
            <div className="field">
              <label htmlFor="presenterAffiliation">Affiliation (所属)</label>
              <input
                id="presenterAffiliation"
                value={presenterAffiliation}
                onChange={(e) => setPresenterAffiliation(e.target.value)}
                placeholder="例: ○○大学 △△研究室"
              />
            </div>

            <button className="button" type="submit">
              Create →
            </button>
          </form>
        </aside>
      </div>
    </div>
  );
}
