// AI Usage Tray - remote view service worker.
//
// Its only job is to make the page installable and to survive a flaky
// connection: the static shell is cached, the usage snapshot never is.
// Bump CACHE whenever a shell file changes: a new cache name is what makes
// the update land.

var CACHE = "ai-usage-tray-shell-v1";

var SHELL = [
  "./",
  "./index.html",
  "./styles.css",
  "./app.js",
  "./config.js",
  "./manifest.webmanifest",
  "./icon-192.png",
  "./icon-512.png"
];

self.addEventListener("install", function (event) {
  event.waitUntil(
    caches.open(CACHE)
      .then(function (cache) { return cache.addAll(SHELL); })
      // A single missing file must not block installation.
      .catch(function () { /* ignore */ })
      .then(function () { return self.skipWaiting(); })
  );
});

self.addEventListener("activate", function (event) {
  event.waitUntil(
    caches.keys()
      .then(function (keys) {
        return Promise.all(keys.map(function (key) {
          return key === CACHE ? null : caches.delete(key);
        }));
      })
      .then(function () { return self.clients.claim(); })
  );
});

self.addEventListener("fetch", function (event) {
  var request = event.request;
  if (request.method !== "GET") return;

  var url;
  try {
    url = new URL(request.url);
  } catch (error) {
    return;
  }

  // Usage snapshots and anything cross-origin go straight to the network,
  // uncached: stale usage numbers would be worse than none.
  if (url.origin !== self.location.origin) return;
  if (url.pathname.indexOf("/u/") !== -1) return;

  // ignoreSearch so a shared link (/?id=…) still matches the cached shell.
  event.respondWith(
    caches.match(request, { ignoreSearch: true }).then(function (hit) {
      if (hit) return hit;

      return fetch(request).then(function (response) {
        if (response && response.ok && response.type === "basic") {
          var copy = response.clone();
          caches.open(CACHE).then(function (cache) {
            cache.put(request, copy);
          }).catch(function () { /* quota or private mode */ });
        }
        return response;
      });
    })
  );
});
