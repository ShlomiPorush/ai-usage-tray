// AI Usage Tray - remote view.
// Reads ?id=<32 hex> from the URL, fetches {apiBase}/u/{id} and renders one card
// per account. Refetches every 60s; relative times re-render every 30s.
// Without an id it shows a landing page (see start()).
// ?id=demo is the one exception: a public sample payload built by the worker.

// The payload uses the product terms "used" and "remaining". The browser
// keeps its older "left" storage value so existing explicit overrides survive.
function resolvePercentMode(storedMode, remoteDisplayMode) {
  if (storedMode === "left" || storedMode === "used") return storedMode;
  return remoteDisplayMode === "remaining" ? "left" : "used";
}

function hasEnabledAlertAccounts(data) {
  return Array.isArray(data && data.accounts) && data.accounts.some(function (account) {
    return account && account.alert && account.alert.enabled === true;
  });
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    resolvePercentMode: resolvePercentMode,
    hasEnabledAlertAccounts: hasEnabledAlertAccounts
  };
}

(function () {
  "use strict";

  // Node loads this file to test the pure preference resolver above.
  if (typeof window === "undefined" || typeof document === "undefined") return;

  var CONFIG = window.REMOTE_VIEW_CONFIG || {};
  var API_BASE = String(CONFIG.apiBase || "").replace(/\/+$/, "");

  var ID_PATTERN = /^[0-9a-f]{32}$/;
  // The one id that is not a 32-hex secret: a public sample served by the worker.
  var DEMO_ID = "demo";
  var REFRESH_MS = 60000;
  var TICK_MS = 30000;
  var STALE_MS = 30 * 60000;

  var THEME_KEY = "aiUsageTray.theme";
  var PERCENT_MODE_KEY = "aiUsageTray.percentMode";
  var LAST_ID_KEY = "aiUsageTray.lastId";

  var RELEASES_URL = "https://github.com/ShlomiPorush/ai-usage-tray/releases/latest";
  var REPO_URL = "https://github.com/ShlomiPorush/ai-usage-tray";

  var SVG_NS = "http://www.w3.org/2000/svg";
  var PROVIDER_ICONS = {
    claude: {
      viewBox: "0 0 100 100",
      path: "M25.71 63.22L41.44 54.39L41.7 53.62L41.44 53.2H40.67L38.04 53.04L29.05 52.79L21.26 52.47L13.71 52.06L11.81 51.66L10.03 49.31L10.21 48.14L11.81 47.07L14.1 47.27L19.16 47.61L26.75 48.14L32.25 48.46L40.41 49.31H41.7L41.88 48.79L41.44 48.46L41.1 48.14L33.24 42.82L24.74 37.19L20.29 33.95L17.88 32.31L16.67 30.77L16.14 27.41L18.33 25.01L21.26 25.21L22.01 25.41L24.99 27.7L31.34 32.62L39.64 38.73L40.85 39.74L41.34 39.4L41.4 39.15L40.85 38.24L36.34 30.09L31.52 21.79L29.38 18.35L28.81 16.28C28.61 15.43 28.47 14.73 28.47 13.85L30.96 10.48L32.33 10.03L35.65 10.48L37.05 11.69L39.11 16.41L42.45 23.83L47.63 33.93L49.15 36.93L49.96 39.7L50.26 40.55H50.79V40.06L51.21 34.38L52 27.39L52.77 18.41L53.04 15.88L54.29 12.84L56.78 11.2L58.72 12.14L60.32 14.42L60.1 15.9L59.15 22.07L57.29 31.75L56.07 38.22H56.78L57.59 37.41L60.87 33.06L66.37 26.18L68.8 23.45L71.63 20.43L73.46 19H76.9L79.43 22.76L78.29 26.65L74.75 31.14L71.82 34.94L67.61 40.61L64.98 45.14L65.22 45.51L65.85 45.45L75.36 43.42L80.5 42.49L86.63 41.44L89.4 42.73L89.71 44.05L88.61 46.74L82.06 48.36L74.37 49.9L62.91 52.61L62.77 52.71L62.93 52.91L68.09 53.4L70.3 53.52H75.7L85.76 54.27L88.39 56.01L89.97 58.14L89.71 59.75L85.66 61.82L80.19 60.52L67.45 57.49L63.07 56.4H62.47V56.76L66.11 60.32L72.79 66.35L81.15 74.12L81.57 76.05L80.5 77.56L79.36 77.4L72.02 71.88L69.19 69.39L62.77 63.98H62.35V64.55L63.82 66.72L71.63 78.45L72.04 82.06L71.47 83.23L69.45 83.94L67.22 83.53L62.65 77.12L57.93 69.89L54.13 63.42L53.66 63.68L51.42 87.87L50.36 89.1L47.94 90.03L45.91 88.49L44.84 86L45.91 81.09L47.21 74.67L48.26 69.57L49.21 63.24L49.78 61.13L49.74 60.99L49.27 61.05L44.5 67.61L37.23 77.42L31.48 83.57L30.11 84.12L27.72 82.89L27.94 80.68L29.28 78.72L37.23 68.6L42.03 62.32L45.12 58.7L45.1 58.18H44.92L23.79 71.9L20.03 72.38L18.41 70.87L18.61 68.38L19.38 67.57L25.73 63.2L25.71 63.22Z"
    },
    codex: {
      viewBox: "0 0 100 100",
      path: "M83.77 42.81C84.67 40.11 84.98 37.26 84.68 34.44C84.38 31.62 83.49 28.89 82.05 26.44C77.69 18.84 68.92 14.94 60.35 16.77C57.98 14.13 54.96 12.17 51.59 11.07C48.21 9.97 44.61 9.77 41.14 10.51C37.67 11.24 34.45 12.88 31.81 15.25C29.17 17.62 27.2 20.64 26.1 24.01C23.32 24.58 20.69 25.74 18.4 27.41C16.1 29.07 14.18 31.21 12.78 33.68C8.37 41.26 9.37 50.83 15.25 57.33C14.35 60.03 14.04 62.88 14.34 65.7C14.63 68.52 15.52 71.25 16.96 73.7C21.33 81.3 30.1 85.21 38.67 83.37C40.56 85.49 42.87 87.19 45.46 88.34C48.05 89.5 50.86 90.09 53.7 90.07C62.48 90.08 70.26 84.41 72.94 76.05C75.72 75.48 78.35 74.32 80.64 72.66C82.94 70.99 84.86 68.85 86.26 66.38C90.62 58.81 89.62 49.3 83.77 42.81ZM53.7 84.84C50.2 84.84 46.8 83.61 44.11 81.37L44.58 81.1L60.51 71.9C60.91 71.67 61.24 71.34 61.47 70.94C61.7 70.54 61.82 70.09 61.82 69.63V47.18L68.56 51.07C68.62 51.11 68.67 51.17 68.68 51.25V69.85C68.66 78.12 61.97 84.82 53.7 84.84ZM21.5 71.08C19.74 68.05 19.11 64.49 19.72 61.04L20.19 61.32L36.13 70.52C36.53 70.75 36.98 70.87 37.43 70.87C37.89 70.87 38.34 70.75 38.73 70.52L58.21 59.29V67.06C58.21 67.1 58.2 67.14 58.18 67.18C58.16 67.21 58.13 67.24 58.1 67.27L41.97 76.57C34.8 80.7 25.64 78.25 21.5 71.08ZM17.3 36.39C19.07 33.34 21.87 31.01 25.19 29.81V48.74C25.18 49.19 25.3 49.65 25.53 50.04C25.75 50.44 26.08 50.77 26.48 50.99L45.86 62.17L39.13 66.07C39.09 66.09 39.05 66.1 39.01 66.1C38.97 66.1 38.93 66.09 38.89 66.07L22.79 56.78C15.64 52.63 13.18 43.48 17.3 36.31V36.39ZM72.62 49.24L53.18 37.95L59.9 34.07C59.93 34.05 59.97 34.04 60.02 34.04C60.06 34.04 60.1 34.05 60.13 34.07L76.24 43.38C78.7 44.8 80.7 46.89 82.02 49.41C83.34 51.92 83.91 54.77 83.68 57.6C83.44 60.43 82.4 63.14 80.69 65.4C78.97 67.67 76.64 69.4 73.98 70.39V51.47C73.97 51.01 73.83 50.56 73.6 50.17C73.36 49.79 73.02 49.46 72.62 49.24ZM79.33 39.17L78.85 38.88L62.94 29.61C62.54 29.38 62.09 29.25 61.63 29.25C61.17 29.25 60.72 29.38 60.32 29.61L40.86 40.84V33.06C40.86 33.02 40.87 32.98 40.88 32.95C40.9 32.91 40.92 32.88 40.96 32.86L57.06 23.57C59.53 22.15 62.35 21.46 65.19 21.58C68.04 21.7 70.79 22.63 73.13 24.26C75.46 25.89 77.28 28.15 78.38 30.78C79.48 33.41 79.81 36.3 79.33 39.1V39.17ZM37.19 52.95L30.46 49.07C30.42 49.05 30.39 49.02 30.37 48.99C30.35 48.96 30.33 48.92 30.33 48.88V30.32C30.33 27.47 31.15 24.68 32.68 22.28C34.21 19.88 36.39 17.96 38.97 16.76C41.54 15.55 44.41 15.1 47.24 15.46C50.06 15.83 52.72 16.99 54.91 18.81L54.44 19.07L38.51 28.27C38.12 28.5 37.79 28.83 37.56 29.23C37.33 29.63 37.21 30.08 37.2 30.54L37.19 52.95ZM40.85 45.06L49.52 40.06L58.21 45.06V55.06L49.55 60.06L40.86 55.06L40.85 45.06Z"
    },
    copilot: {
      viewBox: "0 0 96 96",
      path: "M95.667 67.954C92.225 73.933 72.24 88.04 47.997 88.04 23.754 88.04 3.769 73.933.328 67.954c-.216-.375-.307-.796-.328-1.226V55.661c.019-.371.089-.736.226-1.081 1.489-3.738 5.386-9.166 10.417-10.623.667-1.712 1.655-4.215 2.576-6.062-.154-1.414-.208-2.872-.208-4.345 0-5.322 1.128-9.99 4.527-13.466 1.587-1.623 3.557-2.869 5.893-3.805 5.595-4.545 13.563-8.369 24.48-8.369s19.057 3.824 24.652 8.369c2.337.936 4.306 2.182 5.894 3.805 3.399 3.476 4.527 8.144 4.527 13.466 0 1.473-.054 2.931-.208 4.345.921 1.847 1.909 4.35 2.576 6.062 5.03 1.457 8.928 6.885 10.417 10.623.163.41.231.848.231 1.289v10.644c0 .504-.081 1.004-.333 1.441ZM48.686 43.993l-.3.001-1.077-.001c-.423.709-.894 1.39-1.418 2.035-3.078 3.787-7.672 5.964-14.026 5.964-6.897 0-11.952-1.435-15.123-5.032a7.886 7.886 0 0 1-.342-.419l-.39.419v26.326c5.737 3.118 18.05 8.713 31.987 8.713 13.938 0 26.251-5.595 31.988-8.713V46.96l-.39-.419s-.132.181-.342.419c-3.171 3.597-8.226 5.032-15.123 5.032-6.354 0-10.949-2.177-14.026-5.964a17.178 17.178 0 0 1-1.418-2.034h-.066l.066-.001Zm-3.94-11.733c.17-1.326.251-2.513.253-3.573v-.084c-.005-3.077-.678-5.079-1.752-6.308-1.365-1.562-4.184-2.758-10.127-2.115-6.021.652-9.386 2.146-11.294 4.098-1.847 1.889-2.818 4.715-2.818 9.272 0 4.842.698 7.703 2.232 9.443 1.459 1.655 4.332 3.001 10.625 3.001 4.837 0 7.603-1.573 9.371-3.749 1.899-2.336 2.967-5.759 3.51-9.985Zm6.503 0c.543 4.226 1.611 7.649 3.51 9.985 1.768 2.176 4.533 3.749 9.371 3.749 6.292 0 9.165-1.346 10.624-3.001 1.535-1.74 2.232-4.601 2.232-9.443 0-4.557-.97-7.383-2.817-9.272-1.908-1.952-5.274-3.446-11.294-4.098-5.943-.643-8.763.553-10.127 2.115-1.074 1.229-1.747 3.231-1.752 6.308v.084c.002 1.06.083 2.247.253 3.573Zm-2.563 11.734h.066l-.066-.001v.001Z"
    }
  };

  var content = document.getElementById("content");
  var demoBadge = document.getElementById("demo-badge");
  var updatedEl = document.getElementById("updated");
  var staleEl = document.getElementById("staleness");
  var connectionEl = document.getElementById("connection");
  var notificationEl = document.getElementById("notifications");
  var notificationButton = document.getElementById("notification-toggle");
  var notificationLabel = document.getElementById("notification-label");

  var id = null;
  var payload = null; // last payload we managed to render
  var loading = false;
  var lastFetchAt = 0;
  var serviceWorkerPromise = null;
  var pushSubscription = null;
  var notificationBusy = false;
  var associatedPushReadId = null;

  // --- small helpers ---------------------------------------------------

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function setText(node, text) {
    // Only touch the DOM when the text changes: these are live regions and we
    // do not want a screen reader to re-announce an unchanged notice.
    if (node.textContent !== text) node.textContent = text;
  }

  function show(node, text) {
    setText(node, text);
    node.hidden = false;
  }

  function hide(node) {
    node.hidden = true;
    setText(node, "");
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function renderProviderIcon(provider) {
    var key = typeof provider === "string" ? provider.toLowerCase() : "";
    var icon = el("span", "provider-icon provider-icon-" + (key || "unknown"));
    icon.setAttribute("aria-hidden", "true");

    var definition = PROVIDER_ICONS[key];
    if (!definition) {
      icon.classList.add("is-monogram");
      icon.textContent = key === "zai" ? "Z" : (key.charAt(0).toUpperCase() || "?");
      return icon;
    }

    var svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("viewBox", definition.viewBox);
    svg.setAttribute("focusable", "false");
    var path = document.createElementNS(SVG_NS, "path");
    path.setAttribute("d", definition.path);
    svg.appendChild(path);
    icon.appendChild(svg);
    return icon;
  }

  // localStorage throws in some privacy modes; treat it as best-effort.
  function readStored(key) {
    try {
      return window.localStorage.getItem(key);
    } catch (error) {
      return null;
    }
  }

  function writeStored(key, value) {
    try {
      window.localStorage.setItem(key, value);
    } catch (error) { /* nothing we can do, and nothing depends on it */ }
  }

  function pushSupported() {
    return window.isSecureContext &&
      "Notification" in window &&
      "PushManager" in window &&
      "serviceWorker" in navigator;
  }

  function applicationServerKey(value) {
    var padding = "=".repeat((4 - value.length % 4) % 4);
    var binary = atob((value + padding).replace(/-/g, "+").replace(/_/g, "/"));
    return Uint8Array.from(binary, function (character) { return character.charCodeAt(0); });
  }

  function setNotificationButton(state, label, title, disabled) {
    if (!notificationButton) return;
    notificationButton.hidden = false;
    notificationButton.disabled = Boolean(disabled);
    notificationButton.setAttribute("data-state", state);
    notificationButton.setAttribute("aria-label", title);
    notificationButton.title = title;
    if (notificationLabel) notificationLabel.textContent = label;
  }

  function syncNotificationControl() {
    if (!notificationButton || id === DEMO_ID || !pushSupported() || !payload) {
      if (notificationButton) notificationButton.hidden = true;
      return Promise.resolve();
    }

    var alertsConfigured = hasEnabledAlertAccounts(payload);
    return (serviceWorkerPromise || Promise.reject(new Error("service_worker_unavailable")))
      .then(function (registration) {
        if (!registration) throw new Error("service_worker_unavailable");
        return registration.pushManager.getSubscription();
      })
      .then(function (subscription) {
        pushSubscription = subscription;
        if (subscription) {
          var association = associatedPushReadId === id
            ? Promise.resolve()
            : fetch(API_BASE + "/u/" + id + "/push-subscription", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify(subscription.toJSON())
            }).then(function (response) {
              if (!response.ok) throw new Error("subscription_rejected");
              associatedPushReadId = id;
            });
          return association.then(function () {
            setNotificationButton(
              "on",
              alertsConfigured ? "Alerts on" : "Alerts paused",
              alertsConfigured
                ? "Browser alerts are on. Click to turn them off."
                : "Browser alerts are subscribed but no desktop account alert is enabled. Click to turn them off.",
              false
            );
          });
        } else if (!alertsConfigured) {
          notificationButton.hidden = true;
        } else if (Notification.permission === "denied") {
          setNotificationButton(
            "blocked",
            "Alerts blocked",
            "Notifications are blocked in this browser's site settings.",
            true
          );
        } else {
          setNotificationButton(
            "off",
            "Enable alerts",
            "Enable browser alerts on this device.",
            false
          );
        }
      })
      .catch(function () {
        if (hasEnabledAlertAccounts(payload)) {
          setNotificationButton(
            "unavailable",
            "Alerts unavailable",
            "Browser alerts are unavailable on this device.",
            true
          );
        }
      });
  }

  function unregisterBrowserAlerts() {
    if (!pushSubscription) return Promise.resolve();
    var subscription = pushSubscription;
    return fetch(API_BASE + "/u/" + id + "/push-subscription", {
      method: "DELETE",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ endpoint: subscription.endpoint })
    })
      .catch(function () { /* unsubscribe locally even if server cleanup fails */ })
      .then(function () { return subscription.unsubscribe(); })
      .then(function () {
        pushSubscription = null;
        associatedPushReadId = null;
        show(notificationEl, "Browser alerts are off on this device.");
      });
  }

  function registerBrowserAlerts() {
    if (!hasEnabledAlertAccounts(payload)) return Promise.resolve();
    return Notification.requestPermission()
      .then(function (permission) {
        if (permission !== "granted") throw new Error("permission_denied");
        return Promise.all([
          serviceWorkerPromise,
          fetch(API_BASE + "/push/vapid-public-key", {
            cache: "no-store",
            headers: { Accept: "application/json" }
          }).then(function (response) {
            if (!response.ok) throw new Error("push_not_configured");
            return response.json();
          })
        ]);
      })
      .then(function (results) {
        var registration = results[0];
        var configuration = results[1];
        if (!registration) throw new Error("service_worker_unavailable");
        return registration.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: applicationServerKey(configuration.publicKey)
        }).then(function (subscription) {
          return fetch(API_BASE + "/u/" + id + "/push-subscription", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(subscription.toJSON())
          }).then(function (response) {
            if (!response.ok) {
              return subscription.unsubscribe().then(function () {
                throw new Error("subscription_rejected");
              });
            }
            pushSubscription = subscription;
            associatedPushReadId = id;
            return registration.showNotification("Browser alerts are ready", {
              body: "This device will notify you when a selected quota crosses its threshold.",
              icon: "icon-192.png",
              badge: "icon-192.png",
              tag: "usage-alert-test",
              data: { url: window.location.href }
            });
          });
        });
      })
      .then(function () {
        show(notificationEl, "Browser alerts are on for this device.");
      });
  }

  function setupNotifications() {
    if (!notificationButton || !pushSupported()) return;
    notificationButton.addEventListener("click", function () {
      if (notificationBusy) return;
      notificationBusy = true;
      notificationButton.disabled = true;
      var action = pushSubscription ? unregisterBrowserAlerts() : registerBrowserAlerts();
      action.catch(function (error) {
        var message = error && error.message === "permission_denied"
          ? "Notifications were not allowed. You can change this in the browser's site settings."
          : "Browser alerts could not be changed. Try again.";
        show(notificationEl, message);
      }).then(function () {
        notificationBusy = false;
        return syncNotificationControl();
      });
    });
  }

  // --- theme -----------------------------------------------------------
  // Auto (system) -> Light -> Dark -> Auto. Only the two forced modes set
  // data-theme on <html>; Auto removes it and lets the media query decide.

  var THEME_MODES = ["auto", "light", "dark"];
  var THEME_LABELS = { auto: "System", light: "Light", dark: "Dark" };
  var PAGE_COLORS = { light: "#EDE8E0", dark: "#1A2233" };

  var themeButton = document.getElementById("theme-toggle");
  var themeMode = "auto";

  // The two <meta name="theme-color"> tags carry the Auto defaults. For a forced
  // mode both are pinned to the same colour and their media queries dropped, so
  // the browser chrome follows the page instead of the system.
  function syncThemeColor(mode) {
    var metas = document.querySelectorAll('meta[name="theme-color"][data-scheme]');
    for (var i = 0; i < metas.length; i++) {
      var meta = metas[i];
      var scheme = meta.getAttribute("data-scheme");
      if (mode === "light" || mode === "dark") {
        meta.setAttribute("content", PAGE_COLORS[mode]);
        meta.removeAttribute("media");
      } else {
        meta.setAttribute("content", PAGE_COLORS[scheme]);
        meta.setAttribute("media", "(prefers-color-scheme: " + scheme + ")");
      }
    }
  }

  function applyTheme(mode) {
    themeMode = THEME_MODES.indexOf(mode) === -1 ? "auto" : mode;

    if (themeMode === "auto") {
      document.documentElement.removeAttribute("data-theme");
    } else {
      document.documentElement.setAttribute("data-theme", themeMode);
    }
    syncThemeColor(themeMode);

    if (themeButton) {
      var label = "Theme: " + THEME_LABELS[themeMode];
      themeButton.setAttribute("data-mode", themeMode);
      themeButton.setAttribute("aria-label", label);
      themeButton.setAttribute("title", label + " (click to change)");
    }
  }

  function setupTheme() {
    applyTheme(readStored(THEME_KEY) || "auto");
    if (!themeButton) return;

    themeButton.hidden = false;
    themeButton.addEventListener("click", function () {
      var next = THEME_MODES[(THEME_MODES.indexOf(themeMode) + 1) % THEME_MODES.length];
      applyTheme(next);
      writeStored(THEME_KEY, next);
    });
  }

  // --- percentage display ---------------------------------------------
  // Number and fill show either usage or capacity left. The band still comes
  // from canonical usage, so green always means capacity is available and red
  // always means the quota is near exhaustion.

  var percentToggle = document.getElementById("percent-toggle");
  var percentButtons = percentToggle
    ? percentToggle.querySelectorAll("[data-percent-mode]")
    : [];
  var percentMode = "used";
  var hasPercentModeOverride = false;

  function applyPercentMode(mode, renderPayload) {
    percentMode = mode === "left" ? "left" : "used";

    for (var i = 0; i < percentButtons.length; i++) {
      var button = percentButtons[i];
      button.setAttribute(
        "aria-pressed",
        button.getAttribute("data-percent-mode") === percentMode ? "true" : "false"
      );
    }

    if (payload && renderPayload !== false) render();
  }

  function setupPercentToggle() {
    var storedMode = readStored(PERCENT_MODE_KEY);
    hasPercentModeOverride = storedMode === "left" || storedMode === "used";
    applyPercentMode(resolvePercentMode(storedMode, null), false);
    if (!percentToggle) return;

    percentToggle.hidden = false;
    for (var i = 0; i < percentButtons.length; i++) {
      percentButtons[i].addEventListener("click", function () {
        var next = this.getAttribute("data-percent-mode");
        hasPercentModeOverride = true;
        applyPercentMode(next);
        writeStored(PERCENT_MODE_KEY, percentMode);
      });
    }
  }

  // "1d 12h" / "4h 21m" / "12m"
  function formatDuration(ms) {
    var minutes = Math.floor(ms / 60000);
    if (minutes < 1) return "under a minute";
    var days = Math.floor(minutes / 1440);
    var hours = Math.floor((minutes % 1440) / 60);
    if (days > 0) return days + "d " + hours + "h";
    if (hours > 0) return hours + "h " + (minutes % 60) + "m";
    return minutes + "m";
  }

  function formatWhen(date) {
    var sameDay = date.toDateString() === new Date().toDateString();
    if (sameDay) {
      return date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    }
    return date.toLocaleString([], {
      month: "short", day: "numeric", hour: "2-digit", minute: "2-digit"
    });
  }

  function parseDate(value) {
    if (typeof value !== "string" || !value) return null;
    var date = new Date(value);
    return isNaN(date.getTime()) ? null : date;
  }

  // "expires in 28 days". Deliberately not a formatted date: the browser's own
  // locale turned that into Hebrew on an otherwise English page. A day count
  // reads the same everywhere.
  //
  // Counted in calendar days, not in 24h blocks, so "today" means today
  // whatever the hour, and a daylight-saving shift cannot move the number.
  // Returns null once the expiry day itself is behind us; the caller then drops
  // the clause and keeps the rest of the chip.
  function formatDaysUntil(date, now) {
    var days = Math.round((startOfDay(date) - startOfDay(new Date(now))) / 86400000);
    if (days < 0) return null;
    if (days === 0) return "expires today";
    if (days === 1) return "expires in 1 day";
    return "expires in " + days + " days";
  }

  function startOfDay(date) {
    return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
  }

  // used -> band. The number alone decides: green 0-49, yellow 50-74,
  // orange 75-89, red 90-100. The same rule runs on every surface of the
  // product, so one percentage can never wear two colours.
  //
  // Provider-reported severity is still carried in the payload (the schema is
  // untouched) but deliberately has no say here: a provider that called 71%
  // "normal" used to paint it green while the widget painted it yellow.
  function classify(used) {
    if (used >= 90) return "red";
    if (used >= 75) return "orange";
    if (used >= 50) return "yellow";
    return "green";
  }

  var STATE_RANK = { green: 1, yellow: 2, orange: 3, red: 4 };

  function clampPercent(value) {
    var number = typeof value === "number" ? value : Number(value);
    if (!isFinite(number)) return 0;
    return Math.min(100, Math.max(0, number));
  }

  // --- rendering -------------------------------------------------------

  function renderMessage(heading, paragraphs) {
    clear(content);
    var box = el("section", "message");
    box.appendChild(el("h2", null, heading));
    paragraphs.forEach(function (part) {
      var p = el("p");
      if (typeof part === "string") {
        p.textContent = part;
      } else {
        p.appendChild(document.createTextNode(part.before || ""));
        p.appendChild(el("span", "example", part.example));
        p.appendChild(document.createTextNode(part.after || ""));
      }
      box.appendChild(p);
    });
    content.appendChild(box);
  }

  function link(className, href, text) {
    var node = el("a", className, text);
    node.href = href;
    node.rel = "noopener";
    return node;
  }

  // Shown when the link carries no id: explain what the page is and where the
  // link comes from, nothing more.
  function renderLanding() {
    clear(content);

    var box = el("section", "landing");
    box.appendChild(el("h2", null, "AI Usage Tray"));
    box.appendChild(el("p", null,
      "This page shows live AI subscription usage (Claude, Codex, Z.AI and " +
      "Copilot) shared from the AI Usage Tray app for Windows. To get your own " +
      "link, install the app, enable Settings → Remote view and press Copy link."));

    var actions = el("div", "landing-actions");
    actions.appendChild(link("button button-primary", RELEASES_URL, "Download for Windows"));
    actions.appendChild(link("button button-secondary", REPO_URL, "GitHub"));
    box.appendChild(actions);

    content.appendChild(box);
  }

  function windowsOf(account) {
    return Array.isArray(account.windows) ? account.windows : [];
  }

  // The account dot follows its worst window, by band.
  function worstState(account) {
    var rows = windowsOf(account);
    var worst = null;
    rows.forEach(function (row) {
      if (!row || typeof row !== "object") return;
      var state = classify(clampPercent(row.usedPercent));
      if (worst === null || STATE_RANK[state] > STATE_RANK[worst]) worst = state;
    });
    return worst;
  }

  function orderAccounts(data) {
    var accounts = Array.isArray(data.accounts) ? data.accounts.slice() : [];
    var primaryIndex = -1;
    if (typeof data.primary === "string" && data.primary) {
      for (var i = 0; i < accounts.length; i++) {
        if (accounts[i] && accounts[i].id === data.primary) {
          primaryIndex = i;
          break;
        }
      }
    }
    if (primaryIndex > 0) {
      accounts.unshift(accounts.splice(primaryIndex, 1)[0]);
    }
    return accounts;
  }

  // Number and fill follow the selected view. The band stays tied to canonical
  // usage so its warning meaning remains stable in both modes.
  function renderMeterRow(accountName, source, now) {
    var used = clampPercent(source.usedPercent);
    var left = 100 - used;
    var state = classify(used);
    var label = typeof source.label === "string" && source.label ? source.label : "Usage";
    var scope = typeof source.scope === "string" && source.scope ? source.scope : null;
    var displayed = percentMode === "left" ? left : used;
    var shown = Math.round(displayed);
    var displayLabel = percentMode === "left" ? "remaining" : "used";

    var row = el("div", "meter-row is-" + state);

    var top = el("div", "meter-top");
    var labelBox = el("div", "meter-label-row");
    labelBox.appendChild(el("span", "meter-label", label));
    // A model-scoped window: the chip is what separates "Weekly" for the whole
    // account from "Weekly" for one model.
    if (scope) labelBox.appendChild(el("span", "scope-chip", scope));
    top.appendChild(labelBox);
    top.appendChild(el("span", "meter-value", shown + "%"));
    row.appendChild(top);

    var track = el("div", "meter-track");
    track.setAttribute("role", "meter");
    track.setAttribute("aria-valuemin", "0");
    track.setAttribute("aria-valuemax", "100");
    track.setAttribute("aria-valuenow", String(shown));
    track.setAttribute("aria-valuetext", shown + "% " + displayLabel);
    track.setAttribute("aria-label", accountName + ": " + (scope ? scope + " " + label : label));

    var fill = el("div", "meter-fill");
    fill.style.width = displayed + "%";
    track.appendChild(fill);
    row.appendChild(track);

    var resetsAt = parseDate(source.resetsAt);
    if (resetsAt) {
      var delta = resetsAt.getTime() - now;
      row.appendChild(el(
        "div",
        "meter-reset",
        delta > 0 ? "Resets in " + formatDuration(delta) : "Resetting now"
      ));
    }

    return row;
  }

  // Codex hands out redeemable "usage limit reset" credits. They belong to the
  // account rather than to any one window, so they sit under the header as a
  // quiet line of their own. Absent from the payload when there is none.
  function renderResetCredits(source, now) {
    if (!source || typeof source !== "object") return null;

    var available = Math.floor(Number(source.available));
    if (!isFinite(available) || available < 1) return null;

    var text = available === 1
      ? "1 reset available"
      : available + " resets available";

    var expiresAt = parseDate(source.expiresAt);
    if (expiresAt) {
      var expiry = formatDaysUntil(expiresAt, now);
      if (expiry) text += ", " + expiry;
    }

    var box = el("div", "resets");
    box.appendChild(el("span", "reset-chip", text));
    return box;
  }

  function renderCard(account, isPrimary, now) {
    var name = typeof account.name === "string" && account.name ? account.name : "Account";
    var card = el("article", "card");

    var head = el("div", "card-head");
    var worst = worstState(account);
    var dotState = account.blocked ? "red" : (worst === null ? "none" : worst);
    var dot = el("span", "dot is-" + dotState);
    dot.setAttribute("aria-hidden", "true");
    head.appendChild(dot);
    head.appendChild(renderProviderIcon(account.provider));

    var heading = el("h2", "account-name", name);
    if (isPrimary) {
      var star = el("span", "star", "★");
      star.setAttribute("aria-hidden", "true");
      heading.appendChild(star);
      heading.appendChild(el("span", "sr-only", " (primary account)"));
    }
    head.appendChild(heading);

    if (typeof account.plan === "string" && account.plan) {
      head.appendChild(el("span", "chip", account.plan));
    }
    card.appendChild(head);

    // The provider is refusing requests right now. That is a different fact
    // from any single window reading 100%, so it gets its own line.
    if (account.blocked) {
      card.appendChild(el("p", "blocked-banner", "Limit reached - requests are being refused."));
    }

    var resets = renderResetCredits(account.resetCredits, now);
    if (resets) card.appendChild(resets);

    var rows = windowsOf(account);
    if (rows.length === 0) {
      card.appendChild(el("p", "card-empty", "No usage windows reported."));
      return card;
    }

    var meters = el("div", "meters");
    rows.forEach(function (source) {
      if (source && typeof source === "object") {
        meters.appendChild(renderMeterRow(name, source, now));
      }
    });
    card.appendChild(meters);
    return card;
  }

  function render() {
    if (!payload) return;

    var now = Date.now();
    var generatedAt = parseDate(payload.generatedAt);

    if (generatedAt) {
      var age = now - generatedAt.getTime();
      if (age < 0) age = 0;
      show(updatedEl, age < 60000
        ? "Updated just now"
        : "Updated " + formatDuration(age) + " ago");

      if (age > STALE_MS) {
        show(staleEl, "The app hasn't reported since " + formatWhen(generatedAt) + ".");
      } else {
        hide(staleEl);
      }
    } else {
      hide(updatedEl);
      hide(staleEl);
    }

    var accounts = orderAccounts(payload);
    if (accounts.length === 0) {
      renderMessage("No accounts yet", [
        "The app is connected but hasn't reported any accounts."
      ]);
      return;
    }

    var list = el("div", "cards");
    accounts.forEach(function (account) {
      if (!account || typeof account !== "object") return;
      var isPrimary = typeof payload.primary === "string" && account.id === payload.primary;
      list.appendChild(renderCard(account, isPrimary, now));
    });

    clear(content);
    content.appendChild(list);
  }

  // --- data ------------------------------------------------------------

  function load() {
    if (loading || !id) return;
    loading = true;

    fetch(API_BASE + "/u/" + id, {
      cache: "no-store",
      headers: { Accept: "application/json" }
    })
      .then(function (response) {
        if (response.status === 404) {
          var missing = new Error("not_found");
          missing.code = 404;
          throw missing;
        }
        if (!response.ok) throw new Error("http_" + response.status);
        return response.json();
      })
      .then(function (data) {
        if (!data || typeof data !== "object") throw new Error("bad_payload");
        payload = data;
        if (!hasPercentModeOverride) {
          applyPercentMode(resolvePercentMode(null, data.displayMode), false);
        }
        lastFetchAt = Date.now();
        hide(connectionEl);
        render();
        syncNotificationControl();
      })
      .catch(function (error) {
        if (error && error.code === 404) {
          payload = null;
          hide(updatedEl);
          hide(staleEl);
          hide(connectionEl);
          renderMessage("No data", [
            "The link may have expired (data expires after about a week " +
            "without the app running) or remote view is disabled."
          ]);
          return;
        }

        // Network or server hiccup: keep whatever is on screen and say so quietly.
        if (payload) {
          show(connectionEl, "Couldn't refresh just now, retrying shortly.");
        } else {
          renderMessage("Can't reach the server", [
            "The usage data couldn't be loaded. This page keeps trying every minute.",
            "If it never loads, check that the remote view address in config.js is correct."
          ]);
        }
      })
      .then(function () {
        loading = false;
      });
  }

  // --- start -----------------------------------------------------------

  // Offline shell only; it needs a secure context and is never required.
  function registerServiceWorker() {
    if (!window.isSecureContext || !navigator.serviceWorker) return;
    serviceWorkerPromise = navigator.serviceWorker.register("sw.js")
      .then(function () { return navigator.serviceWorker.ready; })
      .catch(function () { return null; });
  }

  function resolveId() {
    var params = new URLSearchParams(window.location.search);
    var raw = (params.get("id") || "").trim();
    if (raw === DEMO_ID || ID_PATTERN.test(raw)) return raw;

    // An explicit but unusable ?id= means "show me the landing page". Only a
    // link with no id at all falls back to the last id this device saw. That
    // is what an installed app opens, because start_url carries no id.
    if (params.has("id")) return null;

    var stored = (readStored(LAST_ID_KEY) || "").trim();
    return ID_PATTERN.test(stored) ? stored : null;
  }

  function start() {
    setupTheme();
    registerServiceWorker();
    setupNotifications();

    var resolved = resolveId();
    if (!resolved) {
      renderLanding();
      return;
    }

    // An empty apiBase is allowed: it means the worker is proxied on this origin.
    if (typeof CONFIG.apiBase !== "string" ||
        API_BASE.indexOf("REPLACE-WITH-YOUR-WORKER-URL") !== -1) {
      renderMessage("Not configured yet", [
        "This page hasn't been pointed at a remote view address.",
        { before: "Set ", example: "apiBase", after: " in config.js on the server." }
      ]);
      return;
    }

    setupPercentToggle();
    id = resolved;
    // The demo is never remembered: an installed app must not be left pointing
    // at the sample because someone once opened the demo link on this device.
    if (id === DEMO_ID) {
      if (demoBadge) demoBadge.hidden = false;
    } else {
      writeStored(LAST_ID_KEY, id);
    }
    renderMessage("Loading…", ["Fetching the latest usage snapshot."]);
    load();

    window.setInterval(load, REFRESH_MS);
    window.setInterval(render, TICK_MS);

    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState !== "visible") return;
      render();
      if (Date.now() - lastFetchAt >= REFRESH_MS) load();
    });
  }

  start();
})();
