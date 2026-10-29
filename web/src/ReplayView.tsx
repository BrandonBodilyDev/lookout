import { useEffect, useMemo, useState } from "react";
import { MapView, LEVEL_COLOR, type MapLayout, type MapState } from "./MapView";
import { ClockAndWind, CrewCard, LevelBadge } from "./shared";
import type { AlertEvent, ScenarioSummary, Timeline } from "./types";

const SPEEDS = [1, 2, 4, 8];

/** Replays a scenario timeline computed by the engine: no database, no broker, no sign-in. */
export function ReplayView({ header }: { header: React.ReactNode }) {
  const [scenarios, setScenarios] = useState<ScenarioSummary[]>([]);
  const [scenarioId, setScenarioId] = useState<string | null>(null);
  const [timeline, setTimeline] = useState<Timeline | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [index, setIndex] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(2);

  useEffect(() => {
    fetch("/api/scenarios")
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then((list: ScenarioSummary[]) => {
        setScenarios(list);
        setScenarioId(list[0]?.id ?? null);
      })
      .catch((e) => setError(`Could not reach the Lookout API (${e.message}).`));
  }, []);

  useEffect(() => {
    if (!scenarioId) return;
    setTimeline(null);
    fetch(`/api/scenarios/${scenarioId}/timeline`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then((tl: Timeline) => {
        setTimeline(tl);
        // ?t=20 opens the replay at minute 20, for sharing a specific moment.
        const t = Number(new URLSearchParams(window.location.search).get("t"));
        setIndex(Number.isFinite(t) && t > 0 ? Math.min(Math.round(t / tl.stepMin), tl.frames.length - 1) : 0);
        setPlaying(false);
      })
      .catch((e) => setError(`Could not load scenario (${e.message}).`));
  }, [scenarioId]);

  // Playback: one frame (30 scenario seconds) per tick; 1x = one frame per second.
  useEffect(() => {
    if (!playing || !timeline) return;
    const id = window.setInterval(() => {
      setIndex((i) => {
        if (i >= timeline.frames.length - 1) {
          setPlaying(false);
          return i;
        }
        return i + 1;
      });
    }, 1000 / speed);
    return () => window.clearInterval(id);
  }, [playing, speed, timeline]);

  const layout = useMemo<MapLayout | null>(
    () =>
      timeline && {
        key: `replay:${timeline.id}`,
        origin: timeline.origin,
        routes: timeline.routes,
        zones: timeline.safetyZones,
        crewRoutes: Object.fromEntries(timeline.crews.map((c) => [c.id, c.route])),
        boundsHint: timeline.frames[timeline.frames.length - 1].fire.flatMap((poly) => poly[0]),
      },
    [timeline],
  );

  if (error) return <div className="splash error">{error}</div>;
  if (!timeline || !layout) return <div className="splash">Loading scenario…</div>;

  const frame = timeline.frames[index];
  const last = timeline.frames.length - 1;
  const state: MapState = {
    fire: frame.fire,
    crews: frame.crews.map((c) => ({
      id: c.id,
      known: c.known,
      truth: c.truth,
      level: c.level,
      enRoute: c.enRoute,
      stale: (c.fixAgeMin ?? 0) >= 5,
      cut: c.cut,
      fireAtCut: c.fireAtCut,
      leaveNowZone: c.leaveNowZone,
    })),
  };

  return (
    <div className="app">
      <MapView layout={layout} state={state} />
      <aside className="panel">
        {header}
        {scenarios.length > 1 && (
          <select value={scenarioId ?? ""} onChange={(e) => setScenarioId(e.target.value)} aria-label="Scenario">
            {scenarios.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </select>
        )}

        <section className="scenario">
          <h2>{timeline.name}</h2>
          <p>{timeline.description}</p>
        </section>

        <ClockAndWind clock={frame.clock} t={frame.t} wind={frame.wind} />

        <Playback
          index={index}
          last={last}
          playing={playing}
          speed={speed}
          alerts={timeline.alerts}
          durationMin={timeline.durationMin}
          onIndex={(i) => { setIndex(i); setPlaying(false); }}
          onToggle={() => { if (index >= last) setIndex(0); setPlaying((p) => !p); }}
          onSpeed={setSpeed}
        />

        <section aria-label="Crews">
          <h3 className="section-title">Crews</h3>
          {frame.crews.map((c) => (
            <CrewCard key={c.id} crew={c} meta={timeline.crews.find((m) => m.id === c.id)!} />
          ))}
        </section>

        <AlertLog
          alerts={timeline.alerts.filter((a) => a.t <= frame.t)}
          onJump={(t) => { setIndex(Math.round(t / timeline.stepMin)); setPlaying(false); }}
        />

        <Truth timeline={timeline} />
      </aside>
    </div>
  );
}

function Playback(props: {
  index: number;
  last: number;
  playing: boolean;
  speed: number;
  alerts: AlertEvent[];
  durationMin: number;
  onIndex: (i: number) => void;
  onToggle: () => void;
  onSpeed: (s: number) => void;
}) {
  const marks = props.alerts.filter((a) => a.to === "warning" || a.to === "critical");
  return (
    <section className="playback" aria-label="Replay">
      <button className="play" onClick={props.onToggle} aria-label={props.playing ? "Pause" : "Play"}>
        {props.playing ? "❚❚" : "▶"}
      </button>
      <div className="slider">
        <input
          type="range"
          min={0}
          max={props.last}
          value={props.index}
          onChange={(e) => props.onIndex(Number(e.target.value))}
          aria-label="Scenario time"
        />
        <div className="marks" aria-hidden="true">
          {marks.map((a, i) => (
            <span
              key={i}
              className="mark"
              style={{ left: `${(a.t / props.durationMin) * 100}%`, background: LEVEL_COLOR[a.to] }}
              title={`${a.clock} ${a.crew} ${a.to}`}
            />
          ))}
        </div>
      </div>
      <select value={props.speed} onChange={(e) => props.onSpeed(Number(e.target.value))} aria-label="Playback speed">
        {SPEEDS.map((s) => (
          <option key={s} value={s}>{s}×</option>
        ))}
      </select>
    </section>
  );
}

function AlertLog({ alerts, onJump }: { alerts: AlertEvent[]; onJump: (t: number) => void }) {
  const ordered = useMemo(() => [...alerts].reverse(), [alerts]);
  return (
    <section aria-label="Alert log">
      <h3 className="section-title">Alert log <span className="muted">(what the link carried)</span></h3>
      {ordered.length === 0 ? (
        <p className="muted">No alerts yet.</p>
      ) : (
        <ol className="log">
          {ordered.map((a, i) => (
            <li key={`${a.t}-${a.crew}`} className={i === 0 ? "latest" : ""}>
              <button onClick={() => onJump(a.t)} title="Jump to this moment">
                <span className="log-time">{a.clock}</span>
                <LevelBadge level={a.to} />
                <span className="log-text">{a.text}</span>
                <span className="log-bytes">{a.bytes} B</span>
              </button>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

function Truth({ timeline }: { timeline: Timeline }) {
  return (
    <details className="truth">
      <summary>Scenario truth (hidden from the engine)</summary>
      <ul>
        {timeline.score.routes.map((r) => (
          <li key={r.routeId}>Fire reaches {r.routeId}: {r.truthCut ?? "never"}</li>
        ))}
        {timeline.score.crews.map((c) => (
          <li key={c.crewId}>
            {c.crewId}: safety zone {c.reachedSafetyZone ?? "not reached"}, burnover {c.burnover ?? "none"}
          </li>
        ))}
      </ul>
    </details>
  );
}
