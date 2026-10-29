import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from "@microsoft/signalr";
import { MapView, type DrawState, type MapLayout, type MapState } from "./MapView";
import { ClockAndWind, CrewCard, LevelBadge, compass } from "./shared";
import { api, ApiError, post, put, session } from "./api";
import type {
  AuditEntry, IncidentDetail, IncidentSummary, LiveAlert, LiveFrame, LonLat, NwsConditions, Role, ScenarioSummary,
} from "./types";

/** Open = still needs something: queued, escalated, or sent and awaiting an acknowledgement. Informational messages are done once sent. */
const isOpen = (a: LiveAlert) => a.state === "queued" || a.state === "escalated" || (a.state === "sent" && a.requiresAck);

/** Live incidents: sign in, pick or start an incident, then watch and act on it as it runs. */
export function LiveView({ header }: { header: React.ReactNode }) {
  const [auth, setAuth] = useState(session.get());
  const [incidentId, setIncidentId] = useState<string | null>(() => new URLSearchParams(window.location.search).get("incident"));

  const signOut = () => { session.clear(); setAuth(null); };
  const select = (id: string | null) => {
    setIncidentId(id);
    const url = new URL(window.location.href);
    if (id) url.searchParams.set("incident", id); else url.searchParams.delete("incident");
    window.history.replaceState(null, "", url);
  };

  if (!auth) return <Shell header={header}><SignIn onSignedIn={(token, role) => { session.set(token, role); setAuth({ token, role }); }} /></Shell>;
  if (!incidentId) return <Shell header={header}><IncidentPicker token={auth.token} role={auth.role} onSelect={select} onSignOut={signOut} /></Shell>;
  return <Incident key={incidentId} id={incidentId} token={auth.token} role={auth.role} header={header} onBack={() => select(null)} onSignOut={signOut} />;
}

function Shell({ header, children }: { header: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="app app-single">
      <aside className="panel panel-wide">
        {header}
        {children}
      </aside>
    </div>
  );
}

function SignIn({ onSignedIn }: { onSignedIn: (token: string, role: Role) => void }) {
  const [error, setError] = useState<string | null>(null);
  const signIn = async (role: Role) => {
    try {
      const { token } = await post<{ token: string }>("/api/dev/token", null, { role, name: role === "ic" ? "ic-demo" : "observer-demo" });
      onSignedIn(token, role);
    } catch (e) {
      setError(e instanceof ApiError && e.status === 404
        ? "Live incidents are not enabled on this server (no database configured)."
        : `Sign-in failed: ${(e as Error).message}`);
    }
  };
  return (
    <section className="card">
      <h2>Sign in</h2>
      <p className="muted">
        Development sign-in. The incident commander can start incidents, acknowledge or cancel alerts, and edit routes
        and safety zones. An observer can only watch. A real deployment would use the organisation's identity provider.
      </p>
      <div className="button-row">
        <button className="btn primary" onClick={() => signIn("ic")}>Incident commander</button>
        <button className="btn" onClick={() => signIn("observer")}>Observer</button>
      </div>
      {error && <p className="error-text">{error}</p>}
    </section>
  );
}

function IncidentPicker({ token, role, onSelect, onSignOut }: { token: string; role: Role; onSelect: (id: string) => void; onSignOut: () => void }) {
  const [incidents, setIncidents] = useState<IncidentSummary[] | null>(null);
  const [scenarios, setScenarios] = useState<ScenarioSummary[]>([]);
  const [scenarioId, setScenarioId] = useState("");
  const [rate, setRate] = useState(4);
  const [wind, setWind] = useState<"field" | "nws">("field");
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setIncidents(await api<IncidentSummary[]>("/api/incidents", token));
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) onSignOut();
      else setError((e as Error).message);
    }
  }, [token, onSignOut]);

  useEffect(() => {
    load();
    api<ScenarioSummary[]>("/api/scenarios", null).then((s) => { setScenarios(s); setScenarioId(s[0]?.id ?? ""); });
    const id = window.setInterval(load, 5000);
    return () => window.clearInterval(id);
  }, [load]);

  const start = async () => {
    try {
      const created = await post<{ id: string }>("/api/incidents", token, { scenarioId, clockRate: rate, windSource: wind });
      onSelect(created.id);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <>
      <section className="card">
        <div className="row-between">
          <h2>Incidents</h2>
          <button className="btn small" onClick={onSignOut}>Sign out ({role === "ic" ? "IC" : "observer"})</button>
        </div>
        {incidents === null ? <p className="muted">Loading…</p> : incidents.length === 0 ? (
          <p className="muted">No incidents yet. {role === "ic" ? "Start one below, or run the field simulator." : "Ask the incident commander to start one."}</p>
        ) : (
          <ul className="incident-list">
            {incidents.map((i) => (
              <li key={i.id}>
                <button onClick={() => onSelect(i.id)}>
                  <span className={`status status-${i.status}`}>{i.status}</span>
                  <span className="incident-name">{i.name}</span>
                  <span className="muted">
                    started {new Date(i.startedAt).toLocaleTimeString()} · {i.clockRate}× · wind {i.windSource === "nws" ? "NWS" : "field"}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </section>
      {role === "ic" && (
        <section className="card">
          <h2>Start an incident</h2>
          <p className="muted">
            The engine starts watching at once. Crew fixes, drone surveys and weather arrive over MQTT; run the field simulator
            against the new incident to play a scenario in.
          </p>
          <div className="form-grid">
            <label>Scenario
              <select value={scenarioId} onChange={(e) => setScenarioId(e.target.value)}>
                {scenarios.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </select>
            </label>
            <label>Speed
              <select value={rate} onChange={(e) => setRate(Number(e.target.value))}>
                {[1, 4, 8].map((r) => <option key={r} value={r}>{r}× real time</option>)}
              </select>
            </label>
            <label>Wind
              <select value={wind} onChange={(e) => setWind(e.target.value as "field" | "nws")}>
                <option value="field">Field observations</option>
                <option value="nws">National Weather Service</option>
              </select>
            </label>
          </div>
          <button className="btn primary" onClick={start} disabled={!scenarioId}>Start incident</button>
        </section>
      )}
      {error && <p className="error-text">{error}</p>}
    </>
  );
}

function Incident(props: { id: string; token: string; role: Role; header: React.ReactNode; onBack: () => void; onSignOut: () => void }) {
  const { id, token, role } = props;
  const [detail, setDetail] = useState<IncidentDetail | null>(null);
  const [frame, setFrame] = useState<LiveFrame | null>(null);
  const [alerts, setAlerts] = useState<Map<string, LiveAlert>>(new Map());
  const [connection, setConnection] = useState<HubConnectionState>(HubConnectionState.Disconnected);
  const [closed, setClosed] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const [draw, setDraw] = useState<{ target: string; mode: "route" | "zone"; points: LonLat[] } | null>(null);
  const hubRef = useRef<HubConnection | null>(null);

  const loadDetail = useCallback(async () => {
    try {
      const d = await api<IncidentDetail>(`/api/incidents/${id}`, token);
      setDetail(d);
      setClosed(d.status === "closed");
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) props.onSignOut();
      else setNotice((e as Error).message);
    }
  }, [id, token, props]);

  const upsert = useCallback((a: LiveAlert) => setAlerts((m) => new Map(m).set(a.id, a)), []);

  useEffect(() => {
    loadDetail();
    api<LiveAlert[]>(`/api/incidents/${id}/alerts`, token).then((list) => setAlerts(new Map(list.map((a) => [a.id, a])))).catch(() => {});
    api<LiveFrame | undefined>(`/api/incidents/${id}/frame`, token).then((f) => f && setFrame(f)).catch(() => {});

    const hub = new HubConnectionBuilder()
      .withUrl("/hubs/incident", { accessTokenFactory: () => token })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    hubRef.current = hub;
    hub.on("frame", (f: LiveFrame) => setFrame(f));
    hub.on("alert", (a: LiveAlert) => upsert(a));
    hub.on("closed", () => setClosed(true));
    const update = () => setConnection(hub.state);
    hub.onreconnecting(update);
    hub.onreconnected(async () => { update(); const f = await hub.invoke<LiveFrame | null>("Join", id); if (f) setFrame(f); });
    hub.onclose(update);
    hub.start()
      .then(async () => { update(); const f = await hub.invoke<LiveFrame | null>("Join", id); if (f) setFrame(f); })
      .catch((e) => { update(); setNotice(`Live connection failed: ${e.message}`); });
    return () => { hub.stop(); };
  }, [id, token, loadDetail, upsert]);

  const layout = useMemo<MapLayout | null>(() => detail && {
    key: `live:${detail.id}`,
    origin: detail.safetyZones[0]?.ring[0] ?? [0, 0],
    routes: detail.routes,
    zones: detail.safetyZones,
    crewRoutes: Object.fromEntries(detail.crews.map((c) => [c.id, c.route])),
    boundsPad: 0.6,
  }, [detail]);

  const state: MapState = useMemo(() => ({
    fire: frame?.fire ?? [],
    crews: (frame?.crews ?? []).map((c) => ({
      id: c.id, known: c.known, level: c.level, enRoute: c.enRoute, stale: (c.fixAgeMin ?? 0) >= 5,
      cut: c.cut, fireAtCut: c.fireAtCut, leaveNowZone: c.leaveNowZone,
    })),
  }), [frame]);

  const act = async (alert: LiveAlert, action: "ack" | "cancel", reason?: string) => {
    try {
      upsert(await post<LiveAlert>(`/api/incidents/${id}/alerts/${alert.id}/${action}`, token, { version: alert.version, reason }));
      setNotice(null);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.current) {
        upsert(e.current as LiveAlert);
        setNotice(`${e.message} The alert has been refreshed; check it and try again if still needed.`);
      } else setNotice((e as Error).message);
    }
  };

  const saveDrawing = async () => {
    if (!draw || !detail) return;
    try {
      if (draw.mode === "route") {
        const existing = detail.routes.find((r) => r.id === draw.target)!;
        await put(`/api/incidents/${id}/routes/${draw.target}`, token,
          { name: existing.name, safetyZone: existing.safetyZone, path: draw.points, version: existing.version });
      } else {
        const existing = detail.safetyZones.find((z) => z.id === draw.target);
        await put(`/api/incidents/${id}/zones/${draw.target}`, token,
          { name: existing?.name ?? draw.target, ring: draw.points, version: existing?.version ?? null });
      }
      setDraw(null);
      setNotice(`${draw.mode === "route" ? "Route" : "Safety zone"} ${draw.target} saved; the engine is using it now.`);
      loadDetail();
    } catch (e) {
      setNotice((e as Error).message);
    }
  };

  if (!detail || !layout) return <div className="splash">Loading incident…</div>;

  const drawState: DrawState | null = draw && {
    mode: draw.mode,
    points: draw.points,
    onClick: (p) => setDraw((d) => d && { ...d, points: [...d.points, p] }),
  };
  const ordered = [...alerts.values()].sort((a, b) => b.raisedAt.localeCompare(a.raisedAt));
  const open = ordered.filter(isOpen);

  return (
    <div className="app">
      <MapView layout={layout} state={state} draw={drawState} />
      <aside className="panel">
        {props.header}
        <div className="row-between">
          <button className="btn small" onClick={props.onBack}>← Incidents</button>
          <span className={`conn conn-${connection.toLowerCase()}`}>{closed ? "Incident closed" : connection === HubConnectionState.Connected ? "Live" : connection}</span>
        </div>
        <section className="scenario">
          <h2>{detail.name}</h2>
          <p className="muted">
            {role === "ic" ? "Incident commander" : "Observer"} · {detail.clockRate}× real time · started {new Date(detail.startedAt).toLocaleTimeString()}
          </p>
        </section>

        {frame ? (
          <ClockAndWind clock={frame.clock} t={frame.t} wind={frame.wind}
            note={`Wind: ${frame.wind.source}`} />
        ) : <p className="muted">Waiting for the engine's first update…</p>}

        {frame && (
          <p className="muted survey-line">
            {frame.frontAgeSec != null
              ? `Fire front: ${frame.frontSource ?? "drone survey"}, ${Math.round(frame.frontAgeSec)} s ago`
              : "Fire front: no drone survey received yet"}
          </p>
        )}

        {notice && <p className="notice" role="status">{notice}</p>}

        <AlertsPanel alerts={ordered} open={open.length} role={role} onAct={act} />

        <section aria-label="Crews">
          <h3 className="section-title">Crews</h3>
          {(frame?.crews ?? []).map((c) => (
            <CrewCard key={c.id} crew={c} meta={detail.crews.find((m) => m.id === c.id)!} />
          ))}
        </section>

        {role === "ic" && <DrawTools detail={detail} draw={draw} setDraw={setDraw} onSave={saveDrawing} />}

        <Weather origin={layout.origin} token={token} />
        <AfterActionPanel id={id} token={token} refreshKey={ordered.length + ordered.filter((a) => a.state === "acknowledged").length} />
        <AuditLog id={id} token={token} refreshKey={ordered.length + ordered.filter((a) => a.state === "acknowledged").length} />
      </aside>
    </div>
  );
}

function AlertsPanel(props: { alerts: LiveAlert[]; open: number; role: Role; onAct: (a: LiveAlert, action: "ack" | "cancel", reason?: string) => void }) {
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
                {a.state === "escalated" && " · no acknowledgement from the crew: IC action needed"}
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
                    {a.state !== "queued" && <button className="btn small primary" onClick={() => props.onAct(a, "ack")}>Acknowledge for crew</button>}
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

function DrawTools(props: {
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

function Weather({ origin, token }: { origin: LonLat; token: string }) {
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

function AuditLog({ id, token, refreshKey }: { id: string; token: string; refreshKey: number }) {
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

interface Summary {
  crews: {
    crewId: string; name: string; alertsByLevel: Record<string, number>; warningsSent: number; acknowledged: number;
    acknowledgedByIc: number; escalated: number; medianAckScenarioMin: number | null; maxAckScenarioMin: number | null;
    fixesReceived: number; longestFixGapScenarioMin: number | null;
  }[];
  link: { downlinkMessages: number; downlinkBytes: number; uplinkPositionReports: number; uplinkAcks: number; uplinkBytes: number; largestDownlinkBytes: number };
  openAlerts: number;
}

function AfterActionPanel({ id, token, refreshKey }: { id: string; token: string; refreshKey: number }) {
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
            <thead><tr><th>Crew</th><th>Warnings</th><th>Acked</th><th>Median ack</th><th>Escalated</th><th>Longest fix gap</th></tr></thead>
            <tbody>
              {summary.crews.map((c) => (
                <tr key={c.crewId}>
                  <td>{c.crewId}</td>
                  <td>{c.warningsSent}</td>
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
