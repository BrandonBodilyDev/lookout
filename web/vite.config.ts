import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// In development the API runs on :5080 (dotnet run --project src/Lookout.Api).
export default defineConfig({
  plugins: [react()],
  // MapLibre's worker is bundled by Vite (see MapView.tsx) as an ES module worker.
  worker: { format: "es" },
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:5080",
      "/hubs": { target: "http://localhost:5080", ws: true },
    },
  },
});
