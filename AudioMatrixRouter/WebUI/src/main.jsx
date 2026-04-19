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
    // Keep service workers disabled for this app to avoid stale cached bundles on Pages.
    const registrations = await navigator.serviceWorker.getRegistrations();
    await Promise.all(registrations.map((registration) => registration.unregister()));

    if ("caches" in window) {
      const keys = await caches.keys();
      await Promise.all(keys.map((key) => caches.delete(key)));
    }
  });
}
