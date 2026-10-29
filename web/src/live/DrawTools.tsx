import { useState } from "react";
import type { IncidentDetail, LonLat } from "../types";

export function DrawTools(props: {
  detail: IncidentDetail;
  draw: { target: string; mode: "route" | "zone"; points: LonLat[] } | null;
  setDraw: (d: { target: string; mode: "route" | "zone"; points: LonLat[] } | null) => void;
  onSave: () => void;
}) {
  const { detail, draw, setDraw } = props;
  const [target, setTarget] = useState(`route:${detail.routes[0]?.id ?? ""}`);
  const [newZone, setNewZone] = useState("SZ-3");
  if (draw) {
    const enough = draw.mode === "route" ? draw.points.length >= 2 : draw.points.length >= 3;
    return (
      <section className="card">
        <h3 className="section-title">Drawing {draw.mode === "route" ? "route" : "safety zone"} {draw.target}</h3>
        <p className="muted">
          Click the map to add points ({draw.points.length} so far).
          {draw.mode === "route" ? " Start where the crew is and end inside its safety zone." : " Outline the zone; it closes itself."}
        </p>
        <div className="button-row">
          <button className="btn small primary" disabled={!enough} onClick={props.onSave}>Save</button>
          <button className="btn small" disabled={draw.points.length === 0} onClick={() => setDraw({ ...draw, points: draw.points.slice(0, -1) })}>Undo point</button>
          <button className="btn small" onClick={() => setDraw(null)}>Discard</button>
        </div>
      </section>
    );
  }
  const start = () => {
    const [kind, id] = target.split(":");
    if (kind === "route") setDraw({ target: id, mode: "route", points: [] });
    else setDraw({ target: id === "new" ? newZone.trim().toUpperCase() : id, mode: "zone", points: [] });
  };
  return (
    <section className="card">
      <h3 className="section-title">Escape routes and safety zones</h3>
      <div className="form-grid">
        <label>Redraw
          <select value={target} onChange={(e) => setTarget(e.target.value)}>
            {detail.routes.map((r) => <option key={r.id} value={`route:${r.id}`}>Route {r.id} (to {r.safetyZone})</option>)}
            {detail.safetyZones.map((z) => <option key={z.id} value={`zone:${z.id}`}>Safety zone {z.id}</option>)}
            <option value="zone:new">New safety zone…</option>
          </select>
        </label>
        {target === "zone:new" && <label>Zone id<input value={newZone} onChange={(e) => setNewZone(e.target.value)} /></label>}
      </div>
      <button className="btn small" onClick={start}>Start drawing</button>
    </section>
  );
}
