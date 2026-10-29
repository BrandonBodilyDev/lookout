import { useEffect, useState } from "react";
import { api, ApiError } from "../api";

interface Summary {
  crews: {
    crewId: string; name: string; alertsByLevel: Record<string, number>; warningsSent: number; warningsNotDelivered: number; acknowledged: number;
    acknowledgedByIc: number; escalated: number; medianAckScenarioMin: number | null; maxAckScenarioMin: number | null;
    fixesReceived: number; longestFixGapScenarioMin: number | null;
  }[];
  link: { downlinkMessages: number; downlinkBytes: number; uplinkPositionReports: number; uplinkAcks: number; uplinkBytes: number; largestDownlinkBytes: number };
  openAlerts: number;
}

export function AfterActionPanel({ id, token, refreshKey }: { id: string; token: string; refreshKey: number }) {
  const [summary, setSummary] = useState<Summary | null>(null);
  const [denied, setDenied] = useState(false);
  useEffect(() => {
    api<Summary>(`/api/incidents/${id}/summary`, token)
      .then(setSummary)
      .catch((e) => { if (e instanceof ApiError && e.status === 403) setDenied(true); });
  }, [id, token, refreshKey]);

  const downloadCsv = async () => {
    const res = await fetch(`/api/incidents/${id}/audit.csv`, { headers: { authorization: `Bearer ${token}` } });
    if (!res.ok) return;
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement("a");
    a.href = url;
    a.download = `lookout-audit-${id}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  if (denied) return null;
  const min = (v: number | null) => (v == null ? "–" : `${v.toFixed(1)} min`);
  return (
    <details className="card">
      <summary className="section-title">After-action summary</summary>
      {!summary ? <p className="muted">Loading…</p> : (
        <>
          <table className="wx-table">
            <thead><tr><th>Crew</th><th>Warnings</th><th>Not delivered</th><th>Acked</th><th>Median ack</th><th>Escalated</th><th>Longest fix gap</th></tr></thead>
            <tbody>
              {summary.crews.map((c) => (
                <tr key={c.crewId}>
                  <td>{c.crewId}</td>
                  <td>{c.warningsSent}</td>
                  <td>{c.warningsNotDelivered}</td>
                  <td>{c.acknowledged}{c.acknowledgedByIc > 0 && ` (${c.acknowledgedByIc} by IC)`}</td>
                  <td>{min(c.medianAckScenarioMin)}</td>
                  <td>{c.escalated}</td>
                  <td>{min(c.longestFixGapScenarioMin)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted">
            Link: {summary.link.downlinkMessages} downlink messages, {summary.link.downlinkBytes} B (largest {summary.link.largestDownlinkBytes} B);{" "}
            {summary.link.uplinkPositionReports} position reports and {summary.link.uplinkAcks} acks up, {summary.link.uplinkBytes} B. Times are scenario minutes.
          </p>
          <button className="btn small" onClick={downloadCsv}>Download audit trail (CSV)</button>
        </>
      )}
    </details>
  );
}
