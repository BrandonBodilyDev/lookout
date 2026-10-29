import { LEVEL_COLOR } from "./MapView";
import type { CrewView, Level } from "./types";

const COMPASS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
export const compass = (deg: number) => COMPASS[Math.round(deg / 45) % 8];

export function LevelBadge({ level }: { level: Level }) {
  return <span className={`badge badge-${level}`}>{level === "none" ? "OK" : level.toUpperCase()}</span>;
}

export function ClockAndWind(props: {
  clock: string;
  t: number;
  wind: { windFromDeg: number; midflameWindMs: number; headRateMPerMin: number };
  note?: string;
}) {
  const { clock, t, wind, note } = props;
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
          {note && <div className="muted">{note}</div>}
        </div>
      </div>
    </section>
  );
}

export function CrewCard({ crew, meta }: { crew: CrewView; meta: { name: string; route: string; size: number; pace: string; devices?: string[] } }) {
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
          <div className="muted">
            {crew.routeId ?? meta.route} · {meta.size} people · {meta.pace} pace
            {crew.battery != null && ` · battery ${crew.battery}%`}
          </div>
        </div>
        <LevelBadge level={crew.level} />
      </div>
      <div className="crew-headline">{headline}</div>
      {meta.devices && (
        meta.devices.length > 0
          ? <p className="muted devices">Link: {meta.devices.join(", ")}</p>
          : <p className="devices stale">No link device assigned: link alerts wait until one is.</p>
      )}
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
      {crew.routeMargins && crew.routeMargins.length > 1 && !crew.inSafetyZone && (
        <p className="route-margins">
          {crew.routeMargins.map((r) => (
            // Under the 10-minute buffer an alternate is unsafe; below zero the fire beats the crew there.
            <span key={r.routeId} className={r.routeId === crew.routeId ? "best" : r.marginMin !== null && r.marginMin < 0 ? "cut" : r.marginMin !== null && r.marginMin < 10 ? "unsafe" : ""}>
              {r.routeId} {r.marginMin === null ? "safe" : r.marginMin < 0 ? "cut" : `${Math.round(r.marginMin)} min`}
            </span>
          ))}
        </p>
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
