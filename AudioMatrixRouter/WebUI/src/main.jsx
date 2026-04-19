import React from "react";
import ReactDOM from "react-dom/client";
import AppWeb from "./AppWeb";
import AppWindows from "./AppWindows";
import "./index.css";

const isNativeHost = typeof window !== "undefined" && !!window.chrome?.webview;
const App = isNativeHost ? AppWindows : AppWeb;

ReactDOM.createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <App />
  </React.StrictMode>
);

if ("serviceWorker" in navigator) {
  window.addEventListener("load", async () => {
    if (isNativeHost) {
      // Native host serves local assets; service worker can keep stale bundles.
      const registrations = await navigator.serviceWorker.getRegistrations();
      await Promise.all(registrations.map((registration) => registration.unregister()));
      return;
    }

    if (import.meta.env.PROD) {
      const swUrl = `${import.meta.env.BASE_URL}sw.js`;
      navigator.serviceWorker.register(swUrl).catch((error) => {
        console.warn("Service worker registration failed", error);
      });
      return;
    }

    // In development, stale SW caches can cause blank pages after deploy/preview runs.
    const registrations = await navigator.serviceWorker.getRegistrations();
    await Promise.all(registrations.map((registration) => registration.unregister()));
  });
}
