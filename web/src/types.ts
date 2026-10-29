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

// ---- Live incidents (src/Lookout.Api/Live) ----

export type Role = "ic" | "observer";

export interface LiveCrew {
  id: string;
  known: LonLat | null;
  fixAgeMin: number | null;
  battery: number | null;
  enRoute: boolean;
  level: Level;
  rawLevel: Level;
  inSafetyZone: boolean;
  marginMin: number | null;
  slackMin: number | null;
  leaveBy: string | null;
  fireAtCut: string | null;
  cut: LonLat | null;
  leaveNowZone: MultiPolygonCoords | null;
  findings: Finding[];
}

export interface LiveFrame {
  incidentId: string;
  t: number;
  clock: string;
  wind: { windFromDeg: number; midflameWindMs: number; headRateMPerMin: number; source: string };
  fire: MultiPolygonCoords;
  frontAgeSec: number | null;
  crews: LiveCrew[];
}

export type AlertState = "queued" | "sent" | "escalated" | "acknowledged" | "superseded" | "cancelled";

export interface LiveAlert {
  id: string;
  crewId: string;
  linkSeq: number;
  level: Level;
  state: AlertState;
  requiresAck: boolean;
  text: string;
  bytes: number;
  scenarioMin: number;
  raisedAt: string;
  sentAt: string | null;
  sendCount: number;
  escalatedAt: string | null;
  acknowledgedAt: string | null;
  acknowledgedBy: string | null;
  cancelReason: string | null;
  version: number;
}

export interface IncidentSummary {
  id: string;
  scenarioId: string;
  name: string;
  startedAt: string;
  clockRate: number;
  status: "running" | "closed";
  windSource: "field" | "nws";
}

export interface IncidentDetail extends IncidentSummary {
  crews: { id: string; name: string; route: string; size: number; pace: string }[];
  routes: { id: string; name: string; safetyZone: string; path: [number, number, number][]; version: number }[];
  safetyZones: { id: string; name: string; ring: LonLat[]; version: number }[];
}

export interface AuditEntry {
  id: number;
  time: string;
  type: string;
  actor: string;
  role: string;
  crewId: string | null;
  alertId: string | null;
  details: unknown;
}

export interface NwsConditions {
  grid: string;
  station: string;
  observedAt: string;
  windFromDeg: number | null;
  windKmh: number | null;
  gustKmh: number | null;
  relativeHumidity: number | null;
  forecast: { start: string; windKmh: number; windFromDeg: number | null; temperatureF: number; relativeHumidity: number | null; summary: string }[];
}

/** Fields the crew card needs; both replay and live crew frames have them. */
export interface CrewView {
  id: string;
  level: Level;
  enRoute: boolean;
  inSafetyZone: boolean;
  marginMin: number | null;
  slackMin: number | null;
  leaveBy: string | null;
  fireAtCut: string | null;
  fixAgeMin: number | null;
  findings: Finding[];
  battery?: number | null;
}
