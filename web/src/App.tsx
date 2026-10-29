import { useState } from "react";
import { ReplayView } from "./ReplayView";
import { LiveView } from "./LiveView";
import { CrewView } from "./CrewView";

type Mode = "replay" | "live" | "crew";

export default function App() {
  const [mode, setMode] = useState<Mode>(() => {
    const p = new URLSearchParams(window.location.search);
    const m = p.get("mode");
    return m === "crew" ? "crew" : m === "live" || p.has("incident") ? "live" : "replay";
  });

  const switchTo = (m: Mode) => {
    const url = new URL(window.location.href);
    url.searchParams.set("mode", m);
    if (m === "replay") url.searchParams.delete("incident");
    window.history.replaceState(null, "", url);
    setMode(m);
  };

  const header = (
    <header className="brand">
      <div>
        <h1>Lookout</h1>
        <p className="tagline">Escape-route cut-off warnings</p>
      </div>
      <nav className="tabs" aria-label="Mode">
        <button className={mode === "replay" ? "active" : ""} aria-pressed={mode === "replay"} onClick={() => switchTo("replay")}>Replay</button>
        <button className={mode === "live" ? "active" : ""} aria-pressed={mode === "live"} onClick={() => switchTo("live")}>Live</button>
        <button className={mode === "crew" ? "active" : ""} aria-pressed={mode === "crew"} onClick={() => switchTo("crew")}>Crew</button>
      </nav>
    </header>
  );

  if (mode === "crew") return <CrewView header={header} />;
  return mode === "replay" ? <ReplayView header={header} /> : <LiveView header={header} />;
}
