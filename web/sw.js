// AI Usage Tray - remote view service worker.
//
// Its only job is to make the page installable and to survive a flaky
// connection: the static shell is cached, the usage snapshot never is.
// Bump CACHE whenever a shell file changes: a new cache name is what makes
// the update land.

// v12: adds opt-in Web Push notifications for usage resets.
var CACHE = "ai-usage-tray-shell-v12";

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

self.addEventListener("push", function (event) {
  var message;
  try {
    message = event.data ? event.data.json() : null;
  } catch (error) {
    message = null;
  }

  var alerts = Array.isArray(message && message.alerts) ? message.alerts : [];
  var resets = Array.isArray(message && message.resets) ? message.resets : [];
  var displayMode = message && message.displayMode === "remaining" ? "remaining" : "used";
  var viewerUrl = new URL("./", self.registration.scope);
  if (message && typeof message.readId === "string") {
    viewerUrl.searchParams.set("id", message.readId);
  }

  if (alerts.length === 0 && resets.length === 0) {
    event.waitUntil(self.registration.showNotification("AI Usage Tray alert", {
      body: "A configured usage threshold was reached.",
      icon: "icon-192.png",
      badge: "icon-192.png",
      tag: "usage-alert",
      data: { url: viewerUrl.href }
    }));
    return;
  }

  var notifications = alerts.map(function (alert) {
    var used = Math.max(0, Math.min(100, Math.round(Number(alert.usedPercent) || 0)));
    var shown = displayMode === "remaining" ? 100 - used : used;
    var accountName = typeof alert.accountName === "string" && alert.accountName
      ? alert.accountName
      : "Account";
    var windowName = typeof alert.windowLabel === "string" && alert.windowLabel
      ? alert.windowLabel
      : "Usage";
    if (typeof alert.scope === "string" && alert.scope) {
      windowName += " · " + alert.scope;
    }
    return self.registration.showNotification(accountName + " usage alert", {
      body: windowName + " is at " + shown + "% " + displayMode + ".",
      icon: "icon-192.png",
      badge: "icon-192.png",
      tag: "usage-alert:" + String(alert.accountId || "account") + ":" + String(alert.windowKey || "window"),
      renotify: false,
      data: { url: viewerUrl.href }
    });
  });
  notifications = notifications.concat(resets.map(function (reset) {
    var accountName = typeof reset.accountName === "string" && reset.accountName
      ? reset.accountName
      : "Account";
    var windowName = typeof reset.windowLabel === "string" && reset.windowLabel
      ? reset.windowLabel
      : "Usage";
    if (typeof reset.scope === "string" && reset.scope) {
      windowName += " · " + reset.scope;
    }
    return self.registration.showNotification(accountName + " usage reset", {
      body: windowName + " usage reset to 0%.",
      icon: "icon-192.png",
      badge: "icon-192.png",
      tag: "usage-reset:" + String(reset.accountId || "account") + ":" + String(reset.windowKey || "window"),
      renotify: false,
      data: { url: viewerUrl.href }
    });
  }));
  event.waitUntil(Promise.all(notifications));
});

self.addEventListener("notificationclick", function (event) {
  event.notification.close();
  var url = event.notification.data && event.notification.data.url
    ? event.notification.data.url
    : new URL("./", self.registration.scope).href;
  event.waitUntil(
    clients.matchAll({ type: "window", includeUncontrolled: true })
      .then(function (windows) {
        for (var i = 0; i < windows.length; i++) {
          if ("navigate" in windows[i]) {
            return windows[i].navigate(url).then(function (client) { return client.focus(); });
          }
        }
        return clients.openWindow(url);
      })
  );
});
