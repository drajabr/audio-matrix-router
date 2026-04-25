// Audio Matrix Router service worker.
// Strategy:
//   - Hashed assets under /assets/* are immutable: cache-first, kept across versions.
//   - Everything else (index.html, manifest, sw itself) is network-first with cache fallback,
//     so a stale UI never sticks once the network is available.

const CACHE_NAME = "amr-v2";
const APP_SHELL = ["./", "./index.html", "./manifest.webmanifest"];

self.addEventListener("install", (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE_NAME);
    try { await cache.addAll(APP_SHELL); } catch (_) { /* ignore */ }
    await self.skipWaiting();
  })());
});

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter((k) => k !== CACHE_NAME).map((k) => caches.delete(k)));
    await self.clients.claim();
  })());
});

const isHashedAsset = (url) => /\/assets\/.+\.[A-Za-z0-9_-]{6,}\.(js|css|woff2?|ttf|png|jpg|webp|svg)$/i.test(url);

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;

  if (isHashedAsset(url.pathname)) {
    event.respondWith((async () => {
      const cache = await caches.open(CACHE_NAME);
      const cached = await cache.match(req);
      if (cached) return cached;
      const resp = await fetch(req);
      if (resp.ok) cache.put(req, resp.clone());
      return resp;
    })());
    return;
  }

  event.respondWith((async () => {
    try {
      const resp = await fetch(req);
      if (resp.ok) {
        const cache = await caches.open(CACHE_NAME);
        cache.put(req, resp.clone());
      }
      return resp;
    } catch (err) {
      const cache = await caches.open(CACHE_NAME);
      const cached = await cache.match(req) || await cache.match("./index.html");
      if (cached) return cached;
      throw err;
    }
  })());
});
