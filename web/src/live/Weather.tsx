import { useEffect, useState } from "react";
import { compass } from "../shared";
import { api } from "../api";
import type { LonLat, NwsConditions } from "../types";

export function Weather({ origin, token }: { origin: LonLat; token: string }) {
  const [wx, setWx] = useState<NwsConditions | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    api<NwsConditions>(`/api/weather?lat=${origin[1].toFixed(4)}&lon=${origin[0].toFixed(4)}`, token)
      .then(setWx)
      .catch((e) => setError((e as Error).message));
  }, [origin, token]);
  return (
    <details className="card">
      <summary className="section-title">National Weather Service</summary>
      {error && <p className="muted">Unavailable: {error}</p>}
      {wx && (
        <>
          <p>
            {wx.station} at {new Date(wx.observedAt).toLocaleTimeString()}:{" "}
            {wx.windKmh == null ? "wind missing" : wx.windKmh < 1 ? "calm" : `wind from ${compass(wx.windFromDeg ?? 0)} ${(wx.windKmh / 1.609).toFixed(0)} mph`}
            {wx.gustKmh != null && `, gusts ${(wx.gustKmh / 1.609).toFixed(0)} mph`}
            {wx.relativeHumidity != null && `, RH ${wx.relativeHumidity.toFixed(0)}%`}
          </p>
          <table className="wx-table">
            <thead><tr><th>Hour</th><th>Wind</th><th>Temp</th><th>RH</th></tr></thead>
            <tbody>
              {wx.forecast.slice(0, 6).map((h) => (
                <tr key={h.start}>
                  <td>{new Date(h.start).toLocaleTimeString([], { hour: "numeric" })}</td>
                  <td>{h.windFromDeg != null ? compass(h.windFromDeg) : ""} {(h.windKmh / 1.609).toFixed(0)} mph</td>
                  <td>{h.temperatureF.toFixed(0)}°F</td>
                  <td>{h.relativeHumidity != null ? `${h.relativeHumidity}%` : ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted">Grid {wx.grid}. Shown for reference; the engine uses it only when the incident's wind source is NWS.</p>
        </>
      )}
    </details>
  );
}
