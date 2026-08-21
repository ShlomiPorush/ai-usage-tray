// AI Usage Tray - remote view.
// Reads ?id=<32 hex> from the URL, fetches {apiBase}/u/{id} and renders one card
// per account. Refetches every 60s; relative times re-render every 30s.
// Without an id it shows a landing page (see start()).

(function () {
  "use strict";

  var CONFIG = window.REMOTE_VIEW_CONFIG || {};
  var API_BASE = String(CONFIG.apiBase || "").replace(/\/+$/, "");

  var ID_PATTERN = /^[0-9a-f]{32}$/;
  var REFRESH_MS = 60000;
  var TICK_MS = 30000;
  var STALE_MS = 30 * 60000;

  var THEME_KEY = "aiUsageTray.theme";
  var LAST_ID_KEY = "aiUsageTray.lastId";

  var RELEASES_URL = "https://github.com/ShlomiPorush/ai-usage-tray/releases/latest";
  var REPO_URL = "https://github.com/ShlomiPorush/ai-usage-tray";

  var content = document.getElementById("content");
  var updatedEl = document.getElementById("updated");
  var staleEl = document.getElementById("staleness");
  var connectionEl = document.getElementById("connection");

  var id = null;
  var payload = null; // last payload we managed to render
  var loading = false;
  var lastFetchAt = 0;

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

  // --- theme -----------------------------------------------------------
  // Auto (system) -> Light -> Dark -> Auto. Only the two forced modes set
  // data-theme on <html>; Auto removes it and lets the media query decide.

  var THEME_MODES = ["auto", "light", "dark"];
  var THEME_LABELS = { auto: "System", light: "Light", dark: "Dark" };
  var PAGE_COLORS = { light: "#F5F3FA", dark: "#211F30" };

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

  // remaining -> state name. green > 50, amber 20-50, red < 20.
  function classify(remaining) {
    if (remaining > 50) return "good";
    if (remaining >= 20) return "warn";
    return "danger";
  }

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

  function worstRemaining(account) {
    var rows = windowsOf(account);
    if (rows.length === 0) return null;
    return rows.reduce(function (worst, row) {
      var remaining = 100 - clampPercent(row.usedPercent);
      return worst === null || remaining < worst ? remaining : worst;
    }, null);
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

  function renderMeterRow(accountName, source, now) {
    var used = clampPercent(source.usedPercent);
    var remaining = 100 - used;
    var state = classify(remaining);
    var label = typeof source.label === "string" && source.label ? source.label : "Usage";
    var shown = Math.round(remaining);

    var row = el("div", "meter-row is-" + state);

    var top = el("div", "meter-top");
    top.appendChild(el("span", "meter-label", label));

    var value = el("span", "meter-value", shown + "%");
    value.appendChild(el("span", "meter-value-suffix", " left"));
    top.appendChild(value);
    row.appendChild(top);

    var track = el("div", "meter-track");
    track.setAttribute("role", "meter");
    track.setAttribute("aria-valuemin", "0");
    track.setAttribute("aria-valuemax", "100");
    track.setAttribute("aria-valuenow", String(shown));
    track.setAttribute("aria-valuetext", shown + "% remaining");
    track.setAttribute("aria-label", accountName + ": " + label);

    var fill = el("div", "meter-fill");
    fill.style.width = remaining + "%";
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

  function renderCard(account, isPrimary, now) {
    var name = typeof account.name === "string" && account.name ? account.name : "Account";
    var card = el("article", "card");

    var head = el("div", "card-head");
    var worst = worstRemaining(account);
    var dotState = worst === null ? "none" : classify(worst);
    var dot = el("span", "dot is-" + dotState);
    dot.setAttribute("aria-hidden", "true");
    head.appendChild(dot);

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
        lastFetchAt = Date.now();
        hide(connectionEl);
        render();
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
    if (window.location.protocol !== "https:") return;
    if (!navigator.serviceWorker) return;
    window.addEventListener("load", function () {
      navigator.serviceWorker.register("sw.js").catch(function () { /* optional */ });
    });
  }

  function resolveId() {
    var params = new URLSearchParams(window.location.search);
    var raw = (params.get("id") || "").trim();
    if (ID_PATTERN.test(raw)) return raw;

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

    id = resolved;
    writeStored(LAST_ID_KEY, id);
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
