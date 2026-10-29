import { useEffect, useRef } from "react";
import * as maplibregl from "maplibre-gl";
import type { GeoJSONSource, Map as MlMap, MapMouseEvent } from "maplibre-gl";
import workerUrl from "maplibre-gl/dist/maplibre-gl-worker.mjs?worker&url";
import type { Level, LonLat, MultiPolygonCoords } from "./types";

// MapLibre 6 finds its worker relative to its own module URL, which bundling moves.
// Let Vite bundle the worker and hand MapLibre the resulting URL instead.
maplibregl.setWorkerUrl(workerUrl);

// Free, keyless sources. OpenFreeMap for the basemap, Mapterhorn terrarium tiles for hillshade.
const BASEMAP = "https://tiles.openfreemap.org/styles/liberty";
const TERRAIN_TILES = "https://tiles.mapterhorn.com/{z}/{x}/{y}.webp";

export const LEVEL_COLOR: Record<Level, string> = {
  none: "#2f9e5b",
  watch: "#d4a106",
  warning: "#ee7417",
  critical: "#dc2626",
};

/** What does not change every second: where routes and zones are, and which crew uses which route. */
export interface MapLayout {
  /** Changing the key rebuilds the map (new scenario or incident). */
  key: string;
  origin: LonLat;
  routes: { id: string; path: number[][] }[];
  zones: { id: string; ring: LonLat[] }[];
  crewRoutes: Record<string, string>;
  /** Extra points to include when fitting the view, e.g. the final fire extent of a replay. */
  boundsHint?: LonLat[];
  /** Grow the fitted view by this fraction of its size on every side (live mode cannot know the fire's extent yet). */
  boundsPad?: number;
}

export interface MapCrew {
  id: string;
  /** The route the engine is working from right now; it takes the crew's alert colour. */
  routeId?: string;
  known: LonLat | null;
  /** Replay only: where the crew really is, drawn as a ring while its last fix is stale. */
  truth?: LonLat | null;
  level: Level;
  enRoute: boolean;
  stale: boolean;
  cut: LonLat | null;
  fireAtCut: string | null;
  leaveNowZone: MultiPolygonCoords | null;
}

export interface MapState {
  fire: MultiPolygonCoords;
  crews: MapCrew[];
}

export interface DrawState {
  mode: "route" | "zone";
  points: LonLat[];
  onClick: (p: LonLat) => void;
}

type FC = GeoJSON.FeatureCollection;
const fc = (features: GeoJSON.Feature[]): FC => ({ type: "FeatureCollection", features });

function layoutData(layout: MapLayout, state: MapState) {
  const levelByRoute = new Map<string, Level>();
  for (const c of state.crews) {
    const route = c.routeId ?? layout.crewRoutes[c.id];
    if (route) levelByRoute.set(route, c.level);
  }
  return {
    zones: fc(
      layout.zones.map((z) => ({
        type: "Feature",
        properties: { id: z.id },
        geometry: { type: "Polygon", coordinates: [z.ring] },
      })),
    ),
    routes: fc(
      layout.routes.map((r) => ({
        type: "Feature",
        properties: { id: r.id, level: levelByRoute.get(r.id) ?? "none" },
        geometry: { type: "LineString", coordinates: r.path.map(([lon, lat]) => [lon, lat]) },
      })),
    ),
  };
}

function stateData(state: MapState) {
  return {
    fire: fc(
      state.fire.length > 0
        ? [{ type: "Feature", properties: {}, geometry: { type: "MultiPolygon", coordinates: state.fire } }]
        : [],
    ),
    leaveNow: fc(
      state.crews
        .filter((c) => c.leaveNowZone && c.level !== "none" && !c.enRoute)
        .map((c) => ({
          type: "Feature",
          properties: { id: c.id, level: c.level },
          geometry: { type: "MultiPolygon", coordinates: c.leaveNowZone! },
        })),
    ),
    crews: fc(
      state.crews
        .filter((c) => c.known)
        .map((c) => ({
          type: "Feature",
          properties: { id: c.id, level: c.level, stale: c.stale },
          geometry: { type: "Point", coordinates: c.known! },
        })),
    ),
    truth: fc(
      state.crews
        .filter((c) => c.stale && c.truth)
        .map((c) => ({ type: "Feature", properties: { id: c.id }, geometry: { type: "Point", coordinates: c.truth! } })),
    ),
    cuts: fc(
      state.crews
        .filter((c) => c.cut && (c.level === "warning" || c.level === "critical") && !c.enRoute)
        .map((c) => ({
          type: "Feature",
          properties: { id: c.id, label: c.fireAtCut ? `Fire ${c.fireAtCut}` : "" },
          geometry: { type: "Point", coordinates: c.cut! },
        })),
    ),
  };
}

function drawData(draw: DrawState | null): FC {
  if (!draw || draw.points.length === 0) return fc([]);
  const features: GeoJSON.Feature[] = draw.points.map((p) => ({
    type: "Feature",
    properties: {},
    geometry: { type: "Point", coordinates: p },
  }));
  if (draw.points.length > 1) {
    const line = draw.mode === "zone" ? [...draw.points, draw.points[0]] : draw.points;
    features.push({ type: "Feature", properties: {}, geometry: { type: "LineString", coordinates: line } });
  }
  return fc(features);
}

const levelColor = [
  "match",
  ["get", "level"],
  "watch", LEVEL_COLOR.watch,
  "warning", LEVEL_COLOR.warning,
  "critical", LEVEL_COLOR.critical,
  LEVEL_COLOR.none,
] as unknown as maplibregl.DataDrivenPropertyValueSpecification<string>;

const setData = (map: MlMap, id: string, data: FC) => (map.getSource(id) as GeoJSONSource | undefined)?.setData(data);

export function MapView({ layout, state, draw = null }: { layout: MapLayout; state: MapState; draw?: DrawState | null }) {
  const container = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MlMap | null>(null);
  const ready = useRef(false);
  const latest = useRef({ layout, state, draw });
  latest.current = { layout, state, draw };

  useEffect(() => {
    const map = new maplibregl.Map({
      container: container.current!,
      style: BASEMAP,
      center: layout.origin,
      zoom: 14,
      attributionControl: { compact: true },
    });
    mapRef.current = map;
    map.addControl(new maplibregl.NavigationControl({ visualizePitch: true }), "top-right");
    map.addControl(new maplibregl.ScaleControl({ unit: "metric" }), "bottom-left");

    map.on("click", (e: MapMouseEvent) => latest.current.draw?.onClick([e.lngLat.lng, e.lngLat.lat]));

    map.on("load", () => {
      const { layout: lo, state: st, draw: dr } = latest.current;
      const l = layoutData(lo, st);
      const d = stateData(st);
      const firstLabelLayer = map.getStyle().layers.find((x: { type: string }) => x.type === "symbol")?.id;

      map.addSource("terrain", {
        type: "raster-dem",
        tiles: [TERRAIN_TILES],
        tileSize: 512,
        encoding: "terrarium",
        maxzoom: 14,
        attribution: "Terrain: Mapterhorn (USGS 3DEP)",
      });
      map.addLayer(
        { id: "hillshade", type: "hillshade", source: "terrain", paint: { "hillshade-exaggeration": 0.45, "hillshade-shadow-color": "#3b3326" } },
        firstLabelLayer,
      );

      map.addSource("zones", { type: "geojson", data: l.zones });
      map.addSource("routes", { type: "geojson", data: l.routes });
      map.addSource("fire", { type: "geojson", data: d.fire });
      map.addSource("leave-now", { type: "geojson", data: d.leaveNow });
      map.addSource("crews", { type: "geojson", data: d.crews });
      map.addSource("truth", { type: "geojson", data: d.truth });
      map.addSource("cuts", { type: "geojson", data: d.cuts });
      map.addSource("draw", { type: "geojson", data: drawData(dr) });

      map.addLayer({ id: "zones-fill", type: "fill", source: "zones", paint: { "fill-color": "#2f9e5b", "fill-opacity": 0.25 } });
      map.addLayer({ id: "zones-line", type: "line", source: "zones", paint: { "line-color": "#1f7a43", "line-width": 2 } });
      map.addLayer({
        id: "zones-label",
        type: "symbol",
        source: "zones",
        layout: { "text-field": ["get", "id"], "text-size": 12, "text-font": ["Noto Sans Bold"] },
        paint: { "text-color": "#14532d", "text-halo-color": "#fff", "text-halo-width": 1.5 },
      });

      // Leave-now zone: if the fire front is inside it, the crew must already be moving.
      map.addLayer({ id: "leave-now-fill", type: "fill", source: "leave-now", paint: { "fill-color": levelColor, "fill-opacity": 0.08 } });
      map.addLayer({
        id: "leave-now-line",
        type: "line",
        source: "leave-now",
        paint: { "line-color": levelColor, "line-width": 2, "line-dasharray": [3, 2], "line-opacity": 0.9 },
      });

      map.addLayer({ id: "fire-fill", type: "fill", source: "fire", paint: { "fill-color": "#e8542f", "fill-opacity": 0.45 } });
      map.addLayer({ id: "fire-line", type: "line", source: "fire", paint: { "line-color": "#b42318", "line-width": 2.5 } });

      map.addLayer({
        id: "routes-casing",
        type: "line",
        source: "routes",
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": "#ffffff", "line-width": 7 },
      });
      map.addLayer({
        id: "routes-line",
        type: "line",
        source: "routes",
        layout: { "line-cap": "round", "line-join": "round" },
        paint: { "line-color": levelColor, "line-width": 4, "line-dasharray": [2, 1] },
      });
      map.addLayer({
        id: "routes-label",
        type: "symbol",
        source: "routes",
        layout: { "symbol-placement": "line", "text-field": ["get", "id"], "text-size": 11, "text-font": ["Noto Sans Bold"] },
        paint: { "text-color": "#1f2937", "text-halo-color": "#fff", "text-halo-width": 1.5 },
      });

      map.addLayer({
        id: "cuts",
        type: "circle",
        source: "cuts",
        paint: { "circle-radius": 9, "circle-color": "rgba(0,0,0,0)", "circle-stroke-color": "#dc2626", "circle-stroke-width": 3 },
      });
      map.addLayer({
        id: "cuts-label",
        type: "symbol",
        source: "cuts",
        layout: { "text-field": ["get", "label"], "text-size": 11, "text-offset": [0, 1.6], "text-font": ["Noto Sans Bold"] },
        paint: { "text-color": "#991b1b", "text-halo-color": "#fff", "text-halo-width": 1.5 },
      });

      map.addLayer({
        id: "truth",
        type: "circle",
        source: "truth",
        paint: { "circle-radius": 7, "circle-color": "rgba(0,0,0,0)", "circle-stroke-color": "#6b7280", "circle-stroke-width": 2, "circle-stroke-opacity": 0.8 },
      });
      map.addLayer({
        id: "crews",
        type: "circle",
        source: "crews",
        paint: {
          "circle-radius": 8,
          "circle-color": levelColor,
          "circle-stroke-color": "#ffffff",
          "circle-stroke-width": 2.5,
          "circle-opacity": ["case", ["get", "stale"], 0.45, 1],
        },
      });
      map.addLayer({
        id: "crews-label",
        type: "symbol",
        source: "crews",
        layout: { "text-field": ["get", "id"], "text-size": 12, "text-offset": [0, -1.6], "text-font": ["Noto Sans Bold"] },
        paint: { "text-color": "#111827", "text-halo-color": "#fff", "text-halo-width": 2 },
      });

      map.addLayer({ id: "draw-line", type: "line", source: "draw", paint: { "line-color": "#2563eb", "line-width": 3, "line-dasharray": [1, 1] } });
      map.addLayer({
        id: "draw-points",
        type: "circle",
        source: "draw",
        filter: ["==", ["geometry-type"], "Point"],
        paint: { "circle-radius": 5, "circle-color": "#2563eb", "circle-stroke-color": "#fff", "circle-stroke-width": 2 },
      });

      const bounds = new maplibregl.LngLatBounds();
      lo.routes.forEach((r) => r.path.forEach(([lon, lat]) => bounds.extend([lon, lat])));
      lo.zones.forEach((z) => z.ring.forEach((p) => bounds.extend(p)));
      (lo.boundsHint ?? []).forEach((p) => bounds.extend(p));
      if (!bounds.isEmpty()) {
        const pad = lo.boundsPad ?? 0;
        const sw = bounds.getSouthWest();
        const ne = bounds.getNorthEast();
        const dx = (ne.lng - sw.lng) * pad;
        const dy = (ne.lat - sw.lat) * pad;
        map.fitBounds([[sw.lng - dx, sw.lat - dy], [ne.lng + dx, ne.lat + dy]], { padding: 40, duration: 0 });
      }

      ready.current = true;
    });

    return () => {
      ready.current = false;
      map.remove();
      mapRef.current = null;
    };
    // The map is created once per layout key; data is pushed by the effects below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [layout.key]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready.current) return;
    const l = layoutData(layout, state);
    const d = stateData(state);
    setData(map, "zones", l.zones);
    setData(map, "routes", l.routes);
    setData(map, "fire", d.fire);
    setData(map, "leave-now", d.leaveNow);
    setData(map, "crews", d.crews);
    setData(map, "truth", d.truth);
    setData(map, "cuts", d.cuts);
  }, [layout, state]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready.current) return;
    setData(map, "draw", drawData(draw));
    map.getCanvas().style.cursor = draw ? "crosshair" : "";
  }, [draw]);

  return (
    <div className="map-wrap">
      <div ref={container} className="map" role="region" aria-label="Incident map" />
      <Legend />
    </div>
  );
}

function Legend() {
  return (
    // Open on wide screens, collapsed on phones; after that the user's toggle wins.
    <details
      className="legend"
      ref={(el) => {
        if (el && !el.dataset.init) {
          el.open = window.innerWidth > 860;
          el.dataset.init = "1";
        }
      }}
    >
      <summary>Map key</summary>
      <ul>
        <li><span className="key key-fire" />Burned area (latest survey)</li>
        <li><span className="key key-leave" />Leave-now zone: fire inside means go</li>
        <li><span className="key key-route" />Escape route, coloured by crew level</li>
        <li><span className="key key-zone" />Safety zone</li>
        <li><span className="key key-cut" />Where the fire would cut the route</li>
        <li><span className="key key-crew" />Crew at last fix (faded when stale)</li>
      </ul>
    </details>
  );
}
