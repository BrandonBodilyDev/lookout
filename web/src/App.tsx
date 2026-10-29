import { useEffect, useMemo, useState } from "react";
import { MapView, LEVEL_COLOR } from "./MapView";
import type { AlertEvent, CrewFrame, Level, ScenarioSummary, Timeline } from "./types";

const SPEEDS = [1, 2, 4, 8];
const COMPASS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
const compass = (deg: number) => COMPASS[Math.round(deg / 45) % 8];

export default function App() {
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

  if (error) return <div className="splash error">{error}</div>;
  if (!timeline) return <div className="splash">Loading scenario…</div>;

  const frame = timeline.frames[index];
  const last = timeline.frames.length - 1;

  return (
    <div className="app">
      <MapView timeline={timeline} frame={frame} />
      <aside className="panel">
        <header className="brand">
          <div>
            <h1>Lookout</h1>
            <p className="tagline">Escape-route cut-off warnings</p>
          </div>
          {scenarios.length > 1 && (
            <select value={scenarioId ?? ""} onChange={(e) => setScenarioId(e.target.value)} aria-label="Scenario">
              {scenarios.map((s) => (
                <option key={s.id} value={s.id}>{s.name}</option>
              ))}
            </select>
          )}
        </header>

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
          stepMin={timeline.stepMin}
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

function ClockAndWind({ clock, t, wind }: { clock: string; t: number; wind: Timeline["frames"][number]["wind"] }) {
  const head = (wind.windFromDeg + 180) % 360;
  return (
    <section className="clock" aria-label="Time and wind">
      <div>
        <div className="clock-time">{clock}</div>
        <div className="muted">T+{t.toFixed(1)} min</div>
      </div>
      <div className="wind">
        <svg viewBox="-20 -20 40 40" width="44" height="44" aria-hidden="true">
          <circle r="18" className="wind-ring" />
          <g transform={`rotate(${head})`}>
            <path d="M0,-13 L6,4 L0,0 L-6,4 Z" className="wind-arrow" />
          </g>
        </svg>
        <div>
          <div className="wind-main">Wind from {compass(wind.windFromDeg)} {wind.midflameWindMs.toFixed(0)} m/s</div>
          <div className="muted">Head spread {wind.headRateMPerMin.toFixed(0)} m/min</div>
        </div>
      </div>
    </section>
  );
}

function Playback(props: {
  index: number;
  last: number;
  playing: boolean;
  speed: number;
  alerts: AlertEvent[];
  stepMin: number;
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

function LevelBadge({ level }: { level: Level }) {
  return (
    <span className={`badge badge-${level}`}>{level === "none" ? "OK" : level.toUpperCase()}</span>
  );
}

function CrewCard({ crew, meta }: { crew: CrewFrame; meta: Timeline["crews"][number] }) {
  const stale = (crew.fixAgeMin ?? 0) >= 5;
  let headline: string;
  if (crew.inSafetyZone) headline = "In safety zone";
  else if (crew.enRoute) headline = "En route to safety zone";
  else if (crew.slackMin !== null && crew.slackMin <= 0) headline = "LEAVE NOW";
  else if (crew.leaveBy && crew.level !== "none") headline = `Leave by ${crew.leaveBy}`;
  else headline = "Working";

  return (
    <article className={`crew crew-${crew.level}`}>
      <div className="crew-top">
        <div>
          <div className="crew-name">{meta.name}</div>
          <div className="muted">{meta.route} · {meta.size} people · {meta.pace} pace</div>
        </div>
        <LevelBadge level={crew.level} />
      </div>
      <div className="crew-headline">{headline}</div>
      {!crew.inSafetyZone && (
        <dl className="crew-stats">
          <div><dt>Margin</dt><dd>{crew.marginMin !== null ? `${crew.marginMin.toFixed(0)} min` : "–"}</dd></div>
          <div><dt>Fire at cut point</dt><dd>{crew.fireAtCut ?? "–"}</dd></div>
          <div className={stale ? "stale" : ""}>
            <dt>Last fix</dt>
            <dd>{crew.fixAgeMin === null ? "none" : crew.fixAgeMin < 1 ? "now" : `${crew.fixAgeMin.toFixed(0)} min ago`}</dd>
          </div>
        </dl>
      )}
      {crew.findings.length > 0 && (
        <ul className="findings">
          {crew.findings.map((f) => (
            <li key={f.rule}>
              <span className="dot" style={{ background: LEVEL_COLOR[f.level] }} />
              <span><strong>{f.rule}</strong> {f.summary}</span>
            </li>
          ))}
        </ul>
      )}
    </article>
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
