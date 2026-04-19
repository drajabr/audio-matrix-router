import fs from "node:fs";
import path from "node:path";
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

const versionFilePath = path.resolve(__dirname, "../../VERSION");
const appVersion = (process.env.VITE_APP_VERSION || fs.readFileSync(versionFilePath, "utf8")).trim();

export default defineConfig(({ mode }) => ({
  base: mode === "web" ? (process.env.VITE_BASE_PATH || "/audio-matrix-router/") : "./",
  define: {
    __APP_VERSION__: JSON.stringify(appVersion),
  },
  plugins: [react()],
}));
