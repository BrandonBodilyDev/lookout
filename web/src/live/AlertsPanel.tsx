import { useState } from "react";
import { LevelBadge } from "../shared";
import type { LiveAlert, Role } from "../types";

/** Open = still needs something: queued, escalated, or sent and awaiting an acknowledgement. Informational messages are done once sent. */
export const isOpen = (a: LiveAlert) => a.state === "queued" || a.state === "escalated" || (a.state === "sent" && a.requiresAck);

export function AlertsPanel(props: { alerts: LiveAlert[]; open: number; role: Role; onAct: (a: LiveAlert, action: "ack" | "cancel", reason?: string) => void }) {
  const [cancelling, setCancelling] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  return (
    <section aria-label="Alerts">
      <h3 className="section-title">Alerts <span className="muted">({props.open} open)</span></h3>
      {props.alerts.length === 0 ? <p className="muted">No alerts yet.</p> : (
        <ol className="log">
          {props.alerts.slice(0, 12).map((a) => (
            <li key={a.id} className={`alert-item state-${a.state}`}>
              <div className="alert-head">
                <LevelBadge level={a.level} />
                <span className={`state-chip state-${a.state}`}>{a.state}</span>
                <span className="log-bytes">#{a.linkSeq} · {a.bytes} B · sent {a.sendCount}×</span>
              </div>
              <div className="log-text">{a.text}</div>
              <div className="muted alert-meta">
                {new Date(a.raisedAt).toLocaleTimeString()}
                {a.acknowledgedBy && ` · acknowledged by ${a.acknowledgedBy}`}
                {a.cancelReason && ` · cancelled: ${a.cancelReason}`}
                {a.state === "escalated" && (a.sentAt ? " · no acknowledgement from the crew: IC action needed" : " · not delivered on the link: IC action needed")}
              </div>
              {props.role === "ic" && a.requiresAck && isOpen(a) && (
                cancelling === a.id ? (
                  <div className="button-row">
                    <input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Reason for cancelling" aria-label="Reason" />
                    <button className="btn small" disabled={!reason.trim()} onClick={() => { props.onAct(a, "cancel", reason); setCancelling(null); setReason(""); }}>Confirm</button>
                    <button className="btn small" onClick={() => setCancelling(null)}>Back</button>
                  </div>
                ) : (
                  <div className="button-row">
                    <button className="btn small primary" onClick={() => props.onAct(a, "ack")}>Acknowledge for crew</button>
                    <button className="btn small" onClick={() => setCancelling(a.id)}>Cancel…</button>
                  </div>
                )
              )}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
