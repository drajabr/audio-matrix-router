import React from "react";
import ReactDOM from "react-dom/client";
import App from "./App";
import "./index.css";

// Defensive: any previously-registered service worker from old PWA builds
// needs to be torn down so cached HTML never overrides the bundled assets
// shipped with the installed desktop build.
if (typeof navigator !== "undefined" && "serviceWorker" in navigator) {
  window.addEventListener("load", async () => {
    try {
      const registrations = await navigator.serviceWorker.getRegistrations();
      await Promise.all(registrations.map((r) => r.unregister()));
      if ("caches" in window) {
        const keys = await caches.keys();
        await Promise.all(keys.map((k) => caches.delete(k)));
      }
    } catch (_) {
      // Non-fatal.
    }
  });
}

ReactDOM.createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);
