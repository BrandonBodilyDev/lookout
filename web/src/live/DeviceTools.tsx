import { useEffect, useState } from "react";
import { heardAgo } from "../shared";
import { api } from "../api";
import type { DeviceStatus, HeardDevice, IncidentDetail } from "../types";

/**
 * Assign link devices (modem IMEI or simulator id) to crews. The server ignores reports from
 * unassigned devices, so any it has heard from recently are listed for one-click assignment.
 */
export function DeviceTools({ detail, token, onChanged }: { detail: IncidentDetail; token: string; onChanged: (notice: string) => void }) {
  const [crew, setCrew] = useState(detail.crews[0]?.id ?? "");
  const [device, setDevice] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [status, setStatus] = useState<DeviceStatus[]>([]);
  const [unassigned, setUnassigned] = useState<HeardDevice[]>([]);
  const [, setNow] = useState(0);

  // Poll: last-heard times move on their own, and an unassigned device can switch on at any moment.
  useEffect(() => {
    let live = true;
    const load = () => {
      api<DeviceStatus[]>(`/api/incidents/${detail.id}/devices`, token).then((d) => live && setStatus(d)).catch(() => {});
      api<HeardDevice[]>("/api/devices/unassigned", token).then((d) => live && setUnassigned(d)).catch(() => {});
      setNow(Date.now());
    };
    load();
    const timer = window.setInterval(load, 10_000);
    return () => { live = false; window.clearInterval(timer); };
  }, [detail, token]);

  const valid = /^[A-Za-z0-9-]{3,32}$/.test(device.trim());
  const call = async (method: "PUT" | "DELETE", crewId: string, deviceId: string, done: string) => {
    try {
      await api(`/api/incidents/${detail.id}/crews/${crewId}/devices/${encodeURIComponent(deviceId)}`, token, { method });
      setError(null);
      setDevice("");
      onChanged(done);
    } catch (e) {
      setError((e as Error).message);
    }
  };
  const assigned = detail.crews.flatMap((c) => c.devices.map((d) => ({ crew: c.id, device: d })));
  return (
    <section className="card">
      <h3 className="section-title">Link devices</h3>
      {assigned.length > 0 ? (
        <ul className="device-list">
          {assigned.map((a) => {
            const st = status.find((s) => s.deviceId === a.device);
            return (
              <li key={a.device}>
                <span>
                  <b>{a.crew}</b> {a.device}
                  <span className="muted device-heard">
                    {heardAgo(st?.lastHeard ?? null)}{st?.batteryPercent != null && ` · battery ${st.batteryPercent}%`}
                  </span>
                </span>
                <button className="btn small" onClick={() => {
                  if (window.confirm(`Remove ${a.device} from ${a.crew}? Its reports will be ignored and alerts will no longer reach it.`))
                    void call("DELETE", a.crew, a.device, `Device ${a.device} removed from ${a.crew}.`);
                }}>Remove</button>
              </li>
            );
          })}
        </ul>
      ) : <p className="muted">No devices assigned. Reports from unassigned devices are ignored.</p>}
      {unassigned.length > 0 && (
        <div className="unassigned" role="status">
          <p><b>Unassigned devices reporting.</b> Their positions are being ignored until assigned to a crew.</p>
          <ul className="device-list">
            {unassigned.map((u) => (
              <li key={u.deviceId}>
                <span>{u.deviceId}<span className="muted device-heard">{heardAgo(u.lastHeard)} · {u.messages} messages</span></span>
                <button className="btn small" onClick={() => setDevice(u.deviceId)}>Use</button>
              </li>
            ))}
          </ul>
        </div>
      )}
      <div className="form-grid">
        <label>Crew
          <select value={crew} onChange={(e) => setCrew(e.target.value)}>
            {detail.crews.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <label>Device id
          <input value={device} placeholder="IMEI or device id" onChange={(e) => setDevice(e.target.value)} />
        </label>
      </div>
      {error && <p className="notice" role="alert">{error}</p>}
      <button className="btn small" disabled={!valid || !crew}
        onClick={() => call("PUT", crew, device.trim(), `Device ${device.trim()} now reports for ${crew}.`)}>Assign</button>
    </section>
  );
}
