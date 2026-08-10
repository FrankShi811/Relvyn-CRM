const CACHE = "ai-sales-os-pwa-v5.5.4-1";
const APP_ROOT = "/Relvyn-CRM/";
const CORE = [
  APP_ROOT,
  `${APP_ROOT}manifest.webmanifest`,
  `${APP_ROOT}pwa-192.png`,
  `${APP_ROOT}pwa-512.png`
];

self.addEventListener("install", event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(CORE)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", event => {
  event.waitUntil(
    caches.keys()
      .then(keys => Promise.all(keys.filter(key => key !== CACHE).map(key => caches.delete(key))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener("fetch", event => {
  if (event.request.method !== "GET" || new URL(event.request.url).origin !== self.location.origin) return;
  if (event.request.mode === "navigate") {
    event.respondWith(
      fetch(event.request).then(response => {
        if (response.ok) {
          const copy = response.clone();
          void caches.open(CACHE).then(cache => cache.put(APP_ROOT, copy));
        }
        return response;
      }).catch(() => caches.match(APP_ROOT))
    );
    return;
  }
  event.respondWith(
    caches.match(event.request).then(cached => cached || fetch(event.request).then(response => {
      if (response.ok) {
        const copy = response.clone();
        void caches.open(CACHE).then(cache => cache.put(event.request, copy));
      }
      return response;
    }).catch(() => {
      return new Response("Offline", { status: 503, statusText: "Offline" });
    }))
  );
});
