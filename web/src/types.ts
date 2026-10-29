// Mirrors the JSON from GET /api/scenarios/{id}/timeline (src/Lookout.Api/TimelineDto.cs).

export type Level = "none" | "watch" | "warning" | "critical";
export type LonLat = [number, number];
/** GeoJSON MultiPolygon coordinates. */
export type MultiPolygonCoords = LonLat[][][];

export interface Finding {
  rule: string;
  level: Level;
  summary: string;
}

export interface CrewFrame {
  id: string;
  truth: LonLat;
  known: LonLat;
  fixAgeMin: number | null;
  enRoute: boolean;
  level: Level;
  inSafetyZone: boolean;
  marginMin: number | null;
  slackMin: number | null;
  leaveBy: string | null;
  fireAtCut: string | null;
  cut: LonLat | null;
  leaveNowZone: MultiPolygonCoords | null;
  findings: Finding[];
  burnedOver: boolean;
}

export interface Frame {
  t: number;
  clock: string;
  wind: { windFromDeg: number; midflameWindMs: number; headRateMPerMin: number };
  fire: MultiPolygonCoords;
  crews: CrewFrame[];
}

export interface AlertEvent {
  t: number;
  clock: string;
  crew: string;
  route: string;
  from: Level;
  to: Level;
  text: string;
  bytes: number;
}

export interface Timeline {
  id: string;
  name: string;
  description: string;
  start: string;
  durationMin: number;
  stepMin: number;
  origin: LonLat;
  safetyZones: { id: string; name: string; ring: LonLat[] }[];
  routes: { id: string; name: string; safetyZone: string; path: [number, number, number][] }[];
  crews: { id: string; name: string; route: string; size: number; pace: string }[];
  frames: Frame[];
  alerts: AlertEvent[];
  score: {
    routes: { routeId: string; truthCutMin: number | null; truthCut: string | null }[];
    crews: {
      crewId: string;
      reachedSafetyZoneMin: number | null;
      reachedSafetyZone: string | null;
      burnoverMin: number | null;
      burnover: string | null;
    }[];
  };
}

export interface ScenarioSummary {
  id: string;
  name: string;
  description: string;
  start: string;
  durationMin: number;
}

export const LEVEL_ORDER: Record<Level, number> = { none: 0, watch: 1, warning: 2, critical: 3 };
