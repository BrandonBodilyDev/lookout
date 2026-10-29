import { useEffect, useState } from "react";
import { api, ApiError } from "../api";
import type { AuditEntry } from "../types";

export function AuditLog({ id, token, refreshKey }: { id: string; token: string; refreshKey: number }) {
  const [entries, setEntries] = useState<AuditEntry[] | null>(null);
  const [denied, setDenied] = useState(false);
  useEffect(() => {
    api<AuditEntry[]>(`/api/incidents/${id}/audit?limit=40`, token)
      .then(setEntries)
      .catch((e) => { if (e instanceof ApiError && e.status === 403) setDenied(true); });
  }, [id, token, refreshKey]);
  if (denied) return null;
  return (
    <details className="card">
      <summary className="section-title">Audit trail <span className="muted">(append-only)</span></summary>
      {!entries ? <p className="muted">Loading…</p> : (
        <ol className="audit">
          {entries.map((e) => (
            <li key={e.id}>
              <span className="log-time">{new Date(e.time).toLocaleTimeString()}</span>
              <code>{e.type}</code>
              <span>{e.crewId ?? ""}</span>
              <span className="muted">{e.actor} ({e.role})</span>
            </li>
          ))}
        </ol>
      )}
    </details>
  );
}
