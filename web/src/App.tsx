import { useState } from "react";
import { ReplayView } from "./ReplayView";
import { LiveView } from "./LiveView";

type Mode = "replay" | "live";

export default function App() {
  const [mode, setMode] = useState<Mode>(() => {
    const p = new URLSearchParams(window.location.search);
    return p.get("mode") === "live" || p.has("incident") ? "live" : "replay";
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
      </nav>
    </header>
  );

  return mode === "replay" ? <ReplayView header={header} /> : <LiveView header={header} />;
}
