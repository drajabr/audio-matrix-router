import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => ({
  base: mode === "web" ? (process.env.VITE_BASE_PATH || "/audio-matrix-router/") : "./",
  plugins: [react()],
}));
