"use client";

import { useRouter } from "next/navigation";
import { FormEvent, useState } from "react";

import { saveDebugAuth } from "../../lib/auth-store";
import { getSupabaseClient, supabaseEnabled } from "../../lib/supabase-client";

export default function LoginPage() {
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [debugUserId, setDebugUserId] = useState("lab-admin");
  const [debugRole, setDebugRole] = useState<"admin" | "member">("admin");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function handleSupabaseLogin(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const client = getSupabaseClient();
      if (!client) throw new Error("Supabase not configured");
      const { error: err } = await client.auth.signInWithPassword({ email, password });
      if (err) throw err;
      router.push("/sessions");
    } catch (err) {
      setError(err instanceof Error ? err.message : "Login failed");
    } finally {
      setBusy(false);
    }
  }

  function handleDebugLogin(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    saveDebugAuth(debugUserId, debugRole);
    router.push("/sessions");
  }

  return (
    <div className="login-screen">
      <div className="login-card panel">
        <div className="panel-title">
          <span className="marker" />
          AUTHENTICATION
          <span className="panel-subtitle">{supabaseEnabled ? "Supabase Auth" : "Debug mode"}</span>
        </div>

        {error && <div className="alert error">{error}</div>}

        {supabaseEnabled ? (
          <form className="stack" onSubmit={handleSupabaseLogin}>
            <div className="field">
              <label htmlFor="email">Email</label>
              <input id="email" type="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
            </div>
            <div className="field">
              <label htmlFor="password">Password</label>
              <input
                id="password"
                type="password"
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </div>
            <button className="button" type="submit" disabled={busy}>
              {busy ? "Authenticating..." : "Sign in →"}
            </button>
            <p className="hint">
              Admin role requires <code>app_metadata.app_role = "admin"</code> in Supabase.
            </p>
          </form>
        ) : (
          <form className="stack" onSubmit={handleDebugLogin}>
            <div className="field">
              <label htmlFor="userId">User ID</label>
              <input id="userId" value={debugUserId} onChange={(e) => setDebugUserId(e.target.value)} />
            </div>
            <div className="field">
              <label htmlFor="role">Role</label>
              <select id="role" value={debugRole} onChange={(e) => setDebugRole(e.target.value as "admin" | "member")}>
                <option value="admin">admin</option>
                <option value="member">member</option>
              </select>
            </div>
            <button className="button" type="submit">
              Continue (debug) →
            </button>
          </form>
        )}
      </div>
    </div>
  );
}
