import { useEffect, useRef } from "react";
import * as maplibregl from "maplibre-gl";
import type { GeoJSONSource, Map as MlMap } from "maplibre-gl";
import workerUrl from "maplibre-gl/dist/maplibre-gl-worker.mjs?worker&url";
import type { Frame, Level, Timeline } from "./types";

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

type FC = GeoJSON.FeatureCollection;
const fc = (features: GeoJSON.Feature[]): FC => ({ type: "FeatureCollection", features });

function staticData(tl: Timeline) {
  return {
    zones: fc(
      tl.safetyZones.map((z) => ({
        type: "Feature",
        properties: { id: z.id, name: z.name },
        geometry: { type: "Polygon", coordinates: [z.ring] },
      })),
    ),
  };
}

function frameData(tl: Timeline, frame: Frame) {
  const levelByRoute = new Map<string, Level>();
  for (const c of frame.crews) {
    const route = tl.crews.find((x) => x.id === c.id)?.route;
    if (route) levelByRoute.set(route, c.level);
  }
  return {
    fire: fc(
      frame.fire.length > 0
        ? [{ type: "Feature", properties: {}, geometry: { type: "MultiPolygon", coordinates: frame.fire } }]
        : [],
    ),
    zonesNow: fc(
      frame.crews
        .filter((c) => c.leaveNowZone && c.level !== "none" && !c.enRoute)
        .map((c) => ({
          type: "Feature",
          properties: { id: c.id, level: c.level },
          geometry: { type: "MultiPolygon", coordinates: c.leaveNowZone! },
        })),
    ),
    routes: fc(
      tl.routes.map((r) => ({
        type: "Feature",
        properties: { id: r.id, level: levelByRoute.get(r.id) ?? "none" },
        geometry: { type: "LineString", coordinates: r.path.map(([lon, lat]) => [lon, lat]) },
      })),
    ),
    crews: fc(
      frame.crews.map((c) => ({
        type: "Feature",
        properties: { id: c.id, level: c.level, stale: (c.fixAgeMin ?? 0) >= 5 },
        geometry: { type: "Point", coordinates: c.known },
      })),
    ),
    truth: fc(
      frame.crews
        .filter((c) => (c.fixAgeMin ?? 0) >= 5)
        .map((c) => ({
          type: "Feature",
          properties: { id: c.id },
          geometry: { type: "Point", coordinates: c.truth },
        })),
    ),
    cuts: fc(
      frame.crews
        .filter((c) => c.cut && (c.level === "warning" || c.level === "critical") && !c.enRoute)
        .map((c) => ({
          type: "Feature",
          properties: { id: c.id, label: c.fireAtCut ? `Fire ${c.fireAtCut}` : "" },
          geometry: { type: "Point", coordinates: c.cut! },
        })),
    ),
  };
}

const levelColor = [
  "match",
  ["get", "level"],
  "watch", LEVEL_COLOR.watch,
  "warning", LEVEL_COLOR.warning,
  "critical", LEVEL_COLOR.critical,
  LEVEL_COLOR.none,
] as unknown as maplibregl.DataDrivenPropertyValueSpecification<string>;

export function MapView({ timeline, frame }: { timeline: Timeline; frame: Frame }) {
  const container = useRef<HTMLDivElement>(null);
  const mapRef = useRef<MlMap | null>(null);
  const ready = useRef(false);
  const latest = useRef({ timeline, frame });
  latest.current = { timeline, frame };

  useEffect(() => {
    const map = new maplibregl.Map({
      container: container.current!,
      style: BASEMAP,
      center: timeline.origin,
      zoom: 14,
      attributionControl: { compact: true },
    });
    mapRef.current = map;
    map.addControl(new maplibregl.NavigationControl({ visualizePitch: true }), "top-right");
    map.addControl(new maplibregl.ScaleControl({ unit: "metric" }), "bottom-left");

    map.on("load", () => {
      const { timeline: tl, frame: fr } = latest.current;
      const s = staticData(tl);
      const d = frameData(tl, fr);
      const firstLabelLayer = map.getStyle().layers.find((l: { type: string }) => l.type === "symbol")?.id;

      map.addSource("terrain", {
        type: "raster-dem",
        tiles: [TERRAIN_TILES],
        tileSize: 512,
        encoding: "terrarium",
        maxzoom: 14,
        attribution: "Terrain: Mapterhorn (USGS 3DEP)",
      });
      map.addLayer(
        {
          id: "hillshade",
          type: "hillshade",
          source: "terrain",
          paint: { "hillshade-exaggeration": 0.45, "hillshade-shadow-color": "#3b3326" },
        },
        firstLabelLayer,
      );

      map.addSource("zones", { type: "geojson", data: s.zones });
      map.addSource("fire", { type: "geojson", data: d.fire });
      map.addSource("routes", { type: "geojson", data: d.routes });
      map.addSource("crews", { type: "geojson", data: d.crews });
      map.addSource("truth", { type: "geojson", data: d.truth });
      map.addSource("cuts", { type: "geojson", data: d.cuts });
      map.addSource("leave-now", { type: "geojson", data: d.zonesNow });

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
        paint: {
          "circle-radius": 7,
          "circle-color": "rgba(0,0,0,0)",
          "circle-stroke-color": "#6b7280",
          "circle-stroke-width": 2,
          "circle-stroke-opacity": 0.8,
        },
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

      const bounds = new maplibregl.LngLatBounds();
      tl.routes.forEach((r) => r.path.forEach(([lon, lat]) => bounds.extend([lon, lat])));
      tl.safetyZones.forEach((z) => z.ring.forEach((p) => bounds.extend(p)));
      tl.frames[tl.frames.length - 1].fire.forEach((poly) => poly[0].forEach((p) => bounds.extend(p)));
      map.fitBounds(bounds, { padding: 60, duration: 0 });

      ready.current = true;
    });

    return () => {
      ready.current = false;
      map.remove();
      mapRef.current = null;
    };
    // The map is created once per scenario; frames are pushed in the effect below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [timeline.id]);

  useEffect(() => {
    const map = mapRef.current;
    if (!map || !ready.current) return;
    const d = frameData(timeline, frame);
    (map.getSource("fire") as GeoJSONSource).setData(d.fire);
    (map.getSource("routes") as GeoJSONSource).setData(d.routes);
    (map.getSource("crews") as GeoJSONSource).setData(d.crews);
    (map.getSource("truth") as GeoJSONSource).setData(d.truth);
    (map.getSource("cuts") as GeoJSONSource).setData(d.cuts);
    (map.getSource("leave-now") as GeoJSONSource).setData(d.zonesNow);
  }, [timeline, frame]);

  return <div ref={container} className="map" role="region" aria-label="Incident map" />;
}
