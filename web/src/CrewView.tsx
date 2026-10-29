import { useCallback, useEffect, useRef, useState } from "react";
import { HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";
import { LevelBadge } from "./shared";
import { api, ApiError, post } from "./api";
import type { IncidentDetail, IncidentSummary, LiveAlert, LiveFrame } from "./types";

const KEY = "lookout.crew";

interface CrewSession { token: string; incidentId: string; crewId: string }

const store = {
  get(): CrewSession | null {
    try { const v = sessionStorage.getItem(KEY); return v ? JSON.parse(v) : null; } catch { return null; }
  },
  set(s: CrewSession) { try { sessionStorage.setItem(KEY, JSON.stringify(s)); } catch { /* memory only */ } },
  clear() { try { sessionStorage.removeItem(KEY); } catch { /* ignore */ } },
};

/**
 * The crew's phone. One status card, one acknowledge button. In the field this runs on the
 * device that receives the satellite downlink; here it is fed by the same API and SignalR stream.
 */
export function CrewView({ header }: { header: React.ReactNode }) {
  const [session, setSession] = useState(store.get());
  if (!session) return <div className="crew-app">{header}<CrewSignIn onDone={(s) => { store.set(s); setSession(s); }} /></div>;
  return <CrewScreen key={`${session.incidentId}:${session.crewId}`} session={session} header={header} onLeave={() => { store.clear(); setSession(null); }} />;
}

function CrewSignIn({ onDone }: { onDone: (s: CrewSession) => void }) {
  const [incidents, setIncidents] = useState<IncidentSummary[] | null>(null);
  const [detail, setDetail] = useState<IncidentDetail | null>(null);
  const [lookupToken, setLookupToken] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    // An observer token is enough to list incidents and crews before signing in as a crew.
    post<{ token: string }>("/api/dev/token", null, { role: "observer", name: "crew-sign-in" })
      .then(({ token }) => { setLookupToken(token); return api<IncidentSummary[]>("/api/incidents", token); })
      .then((list) => setIncidents(list.filter((i) => i.status === "running")))
      .catch((e) => setError(e instanceof ApiError && e.status === 404 ? "Live incidents are not enabled on this server." : (e as Error).message));
  }, []);

  const pickIncident = async (id: string) => setDetail(await api<IncidentDetail>(`/api/incidents/${id}`, lookupToken));
  const pickCrew = async (crewId: string) => {
    const { token } = await post<{ token: string }>("/api/dev/token", null, { role: "crew", crewId, name: `${crewId.toLowerCase()}-phone` });
    onDone({ token, incidentId: detail!.id, crewId });
  };

  return (
    <section className="card">
      <h2>Crew sign-in</h2>
      {error && <p className="error-text">{error}</p>}
      {!detail ? (
        incidents === null ? <p className="muted">Loading incidents…</p> : incidents.length === 0 ? <p className="muted">No running incidents.</p> : (
          <ul className="incident-list">
            {incidents.map((i) => (
              <li key={i.id}><button onClick={() => pickIncident(i.id)}><span className="incident-name">{i.name}</span>
                <span className="muted">started {new Date(i.startedAt).toLocaleTimeString()}</span></button></li>
            ))}
          </ul>
        )
      ) : (
        <>
          <p className="muted">{detail.name}: which crew is this phone with?</p>
          <div className="button-row">
            {detail.crews.map((c) => <button key={c.id} className="btn primary" onClick={() => pickCrew(c.id)}>{c.name}</button>)}
          </div>
        </>
      )}
    </section>
  );
}

function CrewScreen({ session, header, onLeave }: { session: CrewSession; header: React.ReactNode; onLeave: () => void }) {
  const { token, incidentId, crewId } = session;
  const [frame, setFrame] = useState<LiveFrame | null>(null);
  const [alerts, setAlerts] = useState<Map<string, LiveAlert>>(new Map());
  const [conn, setConn] = useState(HubConnectionState.Disconnected);
  const [message, setMessage] = useState<string | null>(null);
  const [gps, setGps] = useState<{ on: boolean; last?: string; accuracy?: number; error?: string }>({ on: false });
  const seen = useRef(new Set<string>());
  const watchId = useRef<number | null>(null);
  const lastSent = useRef(0);
  const seq = useRef(0);

  const upsert = useCallback((a: LiveAlert) => {
    if (a.crewId !== crewId) return;
    setAlerts((m) => new Map(m).set(a.id, a));
    if (!seen.current.has(a.id) && a.requiresAck && (a.state === "sent" || a.state === "escalated")) {
      seen.current.add(a.id);
      navigator.vibrate?.([400, 150, 400, 150, 400]);
      if ("Notification" in window && Notification.permission === "granted")
        new Notification(`Lookout ${a.level.toUpperCase()}`, { body: a.text, tag: `lookout-${crewId}`, requireInteraction: true });
    }
  }, [crewId]);

  useEffect(() => {
    api<LiveAlert[]>(`/api/incidents/${incidentId}/alerts`, token)
      .then((list) => { list.forEach((a) => seen.current.add(a.id)); setAlerts(new Map(list.filter((a) => a.crewId === crewId).map((a) => [a.id, a]))); })
      .catch((e) => { if (e instanceof ApiError && e.status === 401) onLeave(); });
    const hub = new HubConnectionBuilder().withUrl("/hubs/incident", { accessTokenFactory: () => token })
      .withAutomaticReconnect().configureLogging(LogLevel.Warning).build();
    hub.on("frame", setFrame);
    hub.on("alert", upsert);
    const update = () => setConn(hub.state);
    hub.onreconnecting(update);
    hub.onreconnected(() => { update(); hub.invoke("Join", incidentId); });
    hub.onclose(update);
    hub.start().then(async () => { update(); const f = await hub.invoke<LiveFrame | null>("Join", incidentId); if (f) setFrame(f); }).catch(update);
    return () => { hub.stop(); };
  }, [incidentId, crewId, token, upsert, onLeave]);

  useEffect(() => () => { if (watchId.current !== null) navigator.geolocation.clearWatch(watchId.current); }, []);

  const me = frame?.crews.find((c) => c.id === crewId);
  const list = [...alerts.values()].sort((a, b) => b.raisedAt.localeCompare(a.raisedAt));
  const pending = list.find((a) => a.requiresAck && (a.state === "sent" || a.state === "escalated"));

  const acknowledge = async (a: LiveAlert) => {
    try {
      upsert(await post<LiveAlert>(`/api/incidents/${incidentId}/alerts/${a.id}/ack`, token, { version: a.version }));
      setMessage("Acknowledged. The incident commander can see it.");
    } catch (e) {
      if (e instanceof ApiError && e.status === 409 && e.current) { upsert(e.current as LiveAlert); setMessage("That alert changed; showing the latest."); }
      else setMessage((e as Error).message);
    }
  };

  const toggleGps = () => {
    if (gps.on && watchId.current !== null) {
      navigator.geolocation.clearWatch(watchId.current);
      watchId.current = null;
      setGps({ on: false });
      return;
    }
    if (!("geolocation" in navigator)) { setGps({ on: false, error: "This browser has no GPS access." }); return; }
    watchId.current = navigator.geolocation.watchPosition(
      async (pos) => {
        const now = Date.now();
        if (now - lastSent.current < 30_000) return; // a tracker reports about once a minute; 30 s is plenty
        lastSent.current = now;
        try {
          await post(`/api/incidents/${incidentId}/crews/${crewId}/position`, token,
            { lat: pos.coords.latitude, lon: pos.coords.longitude, sequence: ++seq.current });
          setGps({ on: true, last: new Date().toLocaleTimeString(), accuracy: Math.round(pos.coords.accuracy) });
        } catch (e) { setGps((g) => ({ ...g, error: (e as Error).message })); }
      },
      (err) => setGps({ on: false, error: err.message }),
      { enableHighAccuracy: true, maximumAge: 10_000 },
    );
    setGps({ on: true });
  };

  let headline = "Waiting for the engine…";
  if (me) {
    if (me.inSafetyZone) headline = "In safety zone";
    else if (me.enRoute) headline = "Keep moving to the safety zone";
    else if (me.slackMin !== null && me.slackMin <= 0) headline = "LEAVE NOW";
    else if (me.leaveBy && me.level !== "none") headline = `Leave by ${me.leaveBy}`;
    else headline = "No threat to your escape route";
  }
  return (
    <div className="crew-app">
      {header}
      <div className="row-between">
        <span className="crew-id">{crewId} · {frame?.clock ?? "--:--"}</span>
        <span className={`conn conn-${conn.toLowerCase()}`}>{conn === HubConnectionState.Connected ? "Live" : conn}</span>
      </div>

      <section className={`crew-status level-${me?.level ?? "none"}`} aria-live="assertive">
        <div className="crew-status-level">{(me?.level ?? "none") === "none" ? "OK" : me!.level.toUpperCase()}</div>
        <div className="crew-status-headline">{headline}</div>
        {me && !me.inSafetyZone && (
          <div className="crew-status-detail">
            {me.marginMin !== null && <span>Margin {Math.round(me.marginMin)} min</span>}
            {me.fireAtCut && <span>Fire at your route {me.fireAtCut}</span>}
          </div>
        )}
      </section>

      {pending && (
        <section className="card ack-card">
          <p className="ack-text">{pending.text}</p>
          <button className="btn primary ack-button" onClick={() => acknowledge(pending)}>Acknowledge</button>
          {pending.state === "escalated" && <p className="muted">Not acknowledged in time: the incident commander has been told.</p>}
        </section>
      )}
      {message && <p className="notice" role="status">{message}</p>}

      <section className="card">
        <div className="button-row">
          <button className="btn" onClick={toggleGps}>{gps.on ? "Stop sending my GPS" : "Send my phone's GPS"}</button>
          {"Notification" in window && Notification.permission === "default" && (
            <button className="btn" onClick={() => Notification.requestPermission().then(() => setMessage(null))}>Allow alert notifications</button>
          )}
        </div>
        <p className="muted">
          {gps.on ? `GPS on${gps.last ? `: last sent ${gps.last}, ±${gps.accuracy} m` : ", waiting for a fix"}.` : "GPS off: the engine uses this crew's tracker."}
          {gps.error && ` ${gps.error}`} Alerts vibrate and notify while this page is open; a closed page needs Web Push, which this demo does not set up.
        </p>
      </section>

      <section>
        <h3 className="section-title">Recent messages</h3>
        <ol className="log">
          {list.slice(0, 6).map((a) => (
            <li key={a.id} className="alert-item">
              <div className="alert-head"><LevelBadge level={a.level} /><span className={`state-chip state-${a.state}`}>{a.state}</span>
                <span className="log-bytes">{new Date(a.raisedAt).toLocaleTimeString()}</span></div>
              <div className="log-text">{a.text}</div>
            </li>
          ))}
        </ol>
      </section>
      <button className="btn small" onClick={onLeave}>Sign out</button>
    </div>
  );
}
