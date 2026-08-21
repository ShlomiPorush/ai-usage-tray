// AI Usage Tray - remote view worker, bundled.
//
// GENERATED FILE - do not edit by hand.
// Re-create it with:  cd remote/worker && node bundle.mjs
// Sources: worker.js, web/index.html, web/styles.css, web/app.js
//
// Serves the JSON API (PUT/GET /u/{id}) and the viewer page from a single URL.
// Requires one KV binding named USAGE.

// AI Usage Tray - remote view worker.
// Stores a small JSON snapshot per random id and serves it back to the web page.
// The 128-bit id in the path is the only credential, for reads and writes alike.

const ID_RE = /^[a-f0-9]{32}$/;
const MAX_BODY = 16 * 1024; // 16 KB
const TTL_SECONDS = 604800; // 7 days

const CORS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "GET, PUT, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
};

function json(status, obj) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: {
      ...CORS,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

function empty(status) {
  return new Response(null, { status, headers: { ...CORS } });
}

async function put(request, env, id) {
  const type = request.headers.get("Content-Type") || "";
  if (!type.toLowerCase().includes("application/json")) {
    return json(415, { error: "unsupported_media_type" });
  }

  const declared = Number(request.headers.get("Content-Length"));
  if (Number.isFinite(declared) && declared > MAX_BODY) {
    return json(413, { error: "too_large" });
  }

  const body = await request.text();
  // Byte length, not character count: the payload may contain non-ASCII names.
  if (new TextEncoder().encode(body).length > MAX_BODY) {
    return json(413, { error: "too_large" });
  }

  try {
    JSON.parse(body);
  } catch {
    return json(400, { error: "invalid_json" });
  }

  await env.USAGE.put(id, body, { expirationTtl: TTL_SECONDS });
  return empty(204);
}

async function get(env, id) {
  const body = await env.USAGE.get(id);
  if (body === null) return json(404, { error: "not_found" });
  return new Response(body, {
    status: 200,
    headers: {
      ...CORS,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "no-store",
    },
  });
}

const api = {
  async fetch(request, env) {
    if (request.method === "OPTIONS") return empty(204);

    const path = new URL(request.url).pathname;
    const match = /^\/u\/([^/]+)\/?$/.exec(path);
    if (!match) return json(404, { error: "not_found" });

    if (request.method !== "GET" && request.method !== "PUT") {
      return json(405, { error: "method_not_allowed" });
    }
    if (!ID_RE.test(match[1])) return json(400, { error: "invalid_id" });

    return request.method === "PUT"
      ? put(request, env, match[1])
      : get(env, match[1]);
  },
};

const STATIC_CACHE = "public, max-age=300";

const ASSETS = {
  "/index.html": {
    type: "text/html; charset=utf-8",
    body: "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"color-scheme\" content=\"light dark\">\n<meta name=\"robots\" content=\"noindex, nofollow\">\n<title>AI usage</title>\n<link rel=\"icon\" href=\"data:,\">\n<link rel=\"stylesheet\" href=\"styles.css\">\n</head>\n<body>\n<main class=\"page\">\n  <header class=\"page-header\">\n    <h1>AI usage</h1>\n    <p class=\"updated\" id=\"updated\" hidden></p>\n  </header>\n\n  <p class=\"notice notice-stale\" id=\"staleness\" role=\"status\" hidden></p>\n  <p class=\"notice notice-quiet\" id=\"connection\" role=\"status\" hidden></p>\n\n  <div id=\"content\"></div>\n</main>\n\n<script src=\"config.js\"></script>\n<script src=\"app.js\"></script>\n</body>\n</html>\n",
  },
  "/styles.css": {
    type: "text/css; charset=utf-8",
    body: "/* AI Usage Tray - remote view.\n   Light and dark are two hand-picked palettes, not an inversion of one another. */\n\n:root {\n  color-scheme: light dark;\n\n  --page: #F5F3FA;\n  --card: #FFFFFF;\n  --border: #E4E1EF;\n  --chip-bg: #EFECF7;\n\n  --ink: #1E1B2E;\n  --ink-2: #4A4760;\n  --ink-3: #6B6880;\n\n  /* Status: the fill and the dot. Every track is a lighter step of its own hue. */\n  --good: #10B981;\n  --good-track: #D1FAE5;\n  --warn: #F59E0B;\n  --warn-track: #FBE7BE;\n  --danger: #EF4444;\n  --danger-track: #FBD9D9;\n  --none: #9CA3AF;\n  --none-track: #E5E7EB;\n\n  --stale-bg: #FEF6E7;\n  --stale-border: #EFD79B;\n  --stale-ink: #7A4E07;\n\n  --shadow: 0 1px 2px rgba(30, 27, 46, 0.05);\n}\n\n@media (prefers-color-scheme: dark) {\n  :root {\n    --page: #211F30;\n    --card: #2A2840;\n    --border: #3A3752;\n    --chip-bg: #353252;\n\n    --ink: #F1EFFA;\n    --ink-2: #C9C6DE;\n    --ink-3: #A8A5C2;\n\n    --good: #34D399;\n    --good-track: #1F4D40;\n    --warn: #FBBF24;\n    --warn-track: #4E3C16;\n    --danger: #F87171;\n    --danger-track: #4E2A2A;\n    --none: #A1A0B8;\n    --none-track: #3A3852;\n\n    --stale-bg: #3A3016;\n    --stale-border: #6B551F;\n    --stale-ink: #FBD98D;\n\n    --shadow: none;\n  }\n}\n\n* { box-sizing: border-box; }\n\nbody {\n  margin: 0;\n  padding: 20px 16px 48px;\n  background: var(--page);\n  color: var(--ink);\n  font-family: \"Segoe UI\", -apple-system, BlinkMacSystemFont, system-ui, sans-serif;\n  font-size: 15px;\n  line-height: 1.45;\n  -webkit-text-size-adjust: 100%;\n}\n\n.page {\n  max-width: 720px;\n  margin: 0 auto;\n}\n\n.page-header {\n  margin: 0 0 16px;\n}\n\nh1 {\n  margin: 0;\n  font-size: 20px;\n  font-weight: 600;\n  letter-spacing: -0.01em;\n}\n\n.updated {\n  margin: 2px 0 0;\n  color: var(--ink-3);\n  font-size: 13px;\n}\n\n/* Notices ------------------------------------------------------------- */\n\n.notice {\n  margin: 0 0 12px;\n  padding: 10px 12px;\n  border-radius: 10px;\n  font-size: 13px;\n}\n\n.notice-stale {\n  background: var(--stale-bg);\n  border: 1px solid var(--stale-border);\n  color: var(--stale-ink);\n}\n\n.notice-quiet {\n  padding: 0 2px;\n  color: var(--ink-3);\n}\n\n/* Cards --------------------------------------------------------------- */\n\n.cards {\n  display: flex;\n  flex-direction: column;\n  gap: 12px;\n}\n\n.card {\n  background: var(--card);\n  border: 1px solid var(--border);\n  border-radius: 12px;\n  box-shadow: var(--shadow);\n  padding: 14px 16px;\n}\n\n.card-head {\n  display: flex;\n  align-items: center;\n  gap: 8px;\n  flex-wrap: wrap;\n}\n\n.dot {\n  width: 9px;\n  height: 9px;\n  border-radius: 50%;\n  flex: none;\n  background: var(--none);\n}\n\n.dot.is-good { background: var(--good); }\n.dot.is-warn { background: var(--warn); }\n.dot.is-danger { background: var(--danger); }\n\n.account-name {\n  margin: 0;\n  font-size: 15px;\n  font-weight: 600;\n  color: var(--ink);\n  min-width: 0;\n  overflow-wrap: anywhere;\n}\n\n.star {\n  color: var(--ink-3);\n  font-size: 13px;\n  margin-left: 2px;\n}\n\n.chip {\n  margin-left: auto;\n  padding: 2px 8px;\n  border-radius: 999px;\n  background: var(--chip-bg);\n  color: var(--ink-2);\n  font-size: 12px;\n  font-weight: 500;\n  white-space: nowrap;\n}\n\n.meters {\n  margin-top: 12px;\n  display: flex;\n  flex-direction: column;\n  gap: 12px;\n}\n\n.meter-row + .meter-row {\n  border-top: 1px solid var(--border);\n  padding-top: 12px;\n}\n\n.meter-top {\n  display: flex;\n  align-items: baseline;\n  justify-content: space-between;\n  gap: 12px;\n}\n\n.meter-label {\n  color: var(--ink-2);\n  font-size: 13px;\n  min-width: 0;\n  overflow-wrap: anywhere;\n}\n\n.meter-value {\n  color: var(--ink);\n  font-size: 15px;\n  font-weight: 600;\n  white-space: nowrap;\n}\n\n.meter-value-suffix {\n  color: var(--ink-3);\n  font-size: 12px;\n  font-weight: 400;\n}\n\n.meter-track {\n  margin-top: 6px;\n  height: 7px;\n  border-radius: 999px;\n  background: var(--none-track);\n  overflow: hidden;\n}\n\n.meter-fill {\n  height: 100%;\n  border-radius: 999px;\n  background: var(--none);\n  transition: width 240ms ease;\n}\n\n.meter-row.is-good .meter-track { background: var(--good-track); }\n.meter-row.is-good .meter-fill { background: var(--good); }\n.meter-row.is-warn .meter-track { background: var(--warn-track); }\n.meter-row.is-warn .meter-fill { background: var(--warn); }\n.meter-row.is-danger .meter-track { background: var(--danger-track); }\n.meter-row.is-danger .meter-fill { background: var(--danger); }\n\n.meter-reset {\n  margin-top: 5px;\n  color: var(--ink-3);\n  font-size: 12px;\n}\n\n.card-empty {\n  margin: 10px 0 0;\n  color: var(--ink-3);\n  font-size: 13px;\n}\n\n/* Standalone messages -------------------------------------------------- */\n\n.message {\n  background: var(--card);\n  border: 1px solid var(--border);\n  border-radius: 12px;\n  box-shadow: var(--shadow);\n  padding: 18px 16px;\n}\n\n.message h2 {\n  margin: 0 0 6px;\n  font-size: 16px;\n  font-weight: 600;\n}\n\n.message p {\n  margin: 0 0 8px;\n  color: var(--ink-2);\n  font-size: 14px;\n}\n\n.message p:last-child { margin-bottom: 0; }\n\n.message .example {\n  display: inline-block;\n  padding: 2px 6px;\n  border-radius: 6px;\n  background: var(--chip-bg);\n  color: var(--ink-2);\n  font-family: ui-monospace, \"Cascadia Mono\", Consolas, monospace;\n  font-size: 12.5px;\n  overflow-wrap: anywhere;\n}\n\n.sr-only {\n  position: absolute;\n  width: 1px;\n  height: 1px;\n  margin: -1px;\n  padding: 0;\n  border: 0;\n  overflow: hidden;\n  clip: rect(0 0 0 0);\n  clip-path: inset(50%);\n  white-space: nowrap;\n}\n\n@media (prefers-reduced-motion: reduce) {\n  .meter-fill { transition: none; }\n}\n",
  },
  "/app.js": {
    type: "text/javascript; charset=utf-8",
    body: "// AI Usage Tray - remote view.\n// Reads ?id=<32 hex> from the URL, fetches {apiBase}/u/{id} and renders one card\n// per account. Refetches every 60s; relative times re-render every 30s.\n\n(function () {\n  \"use strict\";\n\n  var CONFIG = window.REMOTE_VIEW_CONFIG || {};\n  var API_BASE = String(CONFIG.apiBase || \"\").replace(/\\/+$/, \"\");\n\n  var ID_PATTERN = /^[0-9a-f]{32}$/;\n  var REFRESH_MS = 60000;\n  var TICK_MS = 30000;\n  var STALE_MS = 30 * 60000;\n\n  var content = document.getElementById(\"content\");\n  var updatedEl = document.getElementById(\"updated\");\n  var staleEl = document.getElementById(\"staleness\");\n  var connectionEl = document.getElementById(\"connection\");\n\n  var id = null;\n  var payload = null; // last payload we managed to render\n  var loading = false;\n  var lastFetchAt = 0;\n\n  // --- small helpers ---------------------------------------------------\n\n  function el(tag, className, text) {\n    var node = document.createElement(tag);\n    if (className) node.className = className;\n    if (text !== undefined && text !== null) node.textContent = text;\n    return node;\n  }\n\n  function setText(node, text) {\n    // Only touch the DOM when the text changes: these are live regions and we\n    // do not want a screen reader to re-announce an unchanged notice.\n    if (node.textContent !== text) node.textContent = text;\n  }\n\n  function show(node, text) {\n    setText(node, text);\n    node.hidden = false;\n  }\n\n  function hide(node) {\n    node.hidden = true;\n    setText(node, \"\");\n  }\n\n  function clear(node) {\n    while (node.firstChild) node.removeChild(node.firstChild);\n  }\n\n  // \"1d 12h\" / \"4h 21m\" / \"12m\"\n  function formatDuration(ms) {\n    var minutes = Math.floor(ms / 60000);\n    if (minutes < 1) return \"under a minute\";\n    var days = Math.floor(minutes / 1440);\n    var hours = Math.floor((minutes % 1440) / 60);\n    if (days > 0) return days + \"d \" + hours + \"h\";\n    if (hours > 0) return hours + \"h \" + (minutes % 60) + \"m\";\n    return minutes + \"m\";\n  }\n\n  function formatWhen(date) {\n    var sameDay = date.toDateString() === new Date().toDateString();\n    if (sameDay) {\n      return date.toLocaleTimeString([], { hour: \"2-digit\", minute: \"2-digit\" });\n    }\n    return date.toLocaleString([], {\n      month: \"short\", day: \"numeric\", hour: \"2-digit\", minute: \"2-digit\"\n    });\n  }\n\n  function parseDate(value) {\n    if (typeof value !== \"string\" || !value) return null;\n    var date = new Date(value);\n    return isNaN(date.getTime()) ? null : date;\n  }\n\n  // remaining -> state name. green > 50, amber 20-50, red < 20.\n  function classify(remaining) {\n    if (remaining > 50) return \"good\";\n    if (remaining >= 20) return \"warn\";\n    return \"danger\";\n  }\n\n  function clampPercent(value) {\n    var number = typeof value === \"number\" ? value : Number(value);\n    if (!isFinite(number)) return 0;\n    return Math.min(100, Math.max(0, number));\n  }\n\n  // --- rendering -------------------------------------------------------\n\n  function renderMessage(heading, paragraphs) {\n    clear(content);\n    var box = el(\"section\", \"message\");\n    box.appendChild(el(\"h2\", null, heading));\n    paragraphs.forEach(function (part) {\n      var p = el(\"p\");\n      if (typeof part === \"string\") {\n        p.textContent = part;\n      } else {\n        p.appendChild(document.createTextNode(part.before || \"\"));\n        p.appendChild(el(\"span\", \"example\", part.example));\n        p.appendChild(document.createTextNode(part.after || \"\"));\n      }\n      box.appendChild(p);\n    });\n    content.appendChild(box);\n  }\n\n  function windowsOf(account) {\n    return Array.isArray(account.windows) ? account.windows : [];\n  }\n\n  function worstRemaining(account) {\n    var rows = windowsOf(account);\n    if (rows.length === 0) return null;\n    return rows.reduce(function (worst, row) {\n      var remaining = 100 - clampPercent(row.usedPercent);\n      return worst === null || remaining < worst ? remaining : worst;\n    }, null);\n  }\n\n  function orderAccounts(data) {\n    var accounts = Array.isArray(data.accounts) ? data.accounts.slice() : [];\n    var primaryIndex = -1;\n    if (typeof data.primary === \"string\" && data.primary) {\n      for (var i = 0; i < accounts.length; i++) {\n        if (accounts[i] && accounts[i].id === data.primary) {\n          primaryIndex = i;\n          break;\n        }\n      }\n    }\n    if (primaryIndex > 0) {\n      accounts.unshift(accounts.splice(primaryIndex, 1)[0]);\n    }\n    return accounts;\n  }\n\n  function renderMeterRow(accountName, source, now) {\n    var used = clampPercent(source.usedPercent);\n    var remaining = 100 - used;\n    var state = classify(remaining);\n    var label = typeof source.label === \"string\" && source.label ? source.label : \"Usage\";\n    var shown = Math.round(remaining);\n\n    var row = el(\"div\", \"meter-row is-\" + state);\n\n    var top = el(\"div\", \"meter-top\");\n    top.appendChild(el(\"span\", \"meter-label\", label));\n\n    var value = el(\"span\", \"meter-value\", shown + \"%\");\n    value.appendChild(el(\"span\", \"meter-value-suffix\", \" left\"));\n    top.appendChild(value);\n    row.appendChild(top);\n\n    var track = el(\"div\", \"meter-track\");\n    track.setAttribute(\"role\", \"meter\");\n    track.setAttribute(\"aria-valuemin\", \"0\");\n    track.setAttribute(\"aria-valuemax\", \"100\");\n    track.setAttribute(\"aria-valuenow\", String(shown));\n    track.setAttribute(\"aria-valuetext\", shown + \"% remaining\");\n    track.setAttribute(\"aria-label\", accountName + \" — \" + label);\n\n    var fill = el(\"div\", \"meter-fill\");\n    fill.style.width = remaining + \"%\";\n    track.appendChild(fill);\n    row.appendChild(track);\n\n    var resetsAt = parseDate(source.resetsAt);\n    if (resetsAt) {\n      var delta = resetsAt.getTime() - now;\n      row.appendChild(el(\n        \"div\",\n        \"meter-reset\",\n        delta > 0 ? \"Resets in \" + formatDuration(delta) : \"Resetting now\"\n      ));\n    }\n\n    return row;\n  }\n\n  function renderCard(account, isPrimary, now) {\n    var name = typeof account.name === \"string\" && account.name ? account.name : \"Account\";\n    var card = el(\"article\", \"card\");\n\n    var head = el(\"div\", \"card-head\");\n    var worst = worstRemaining(account);\n    var dotState = worst === null ? \"none\" : classify(worst);\n    var dot = el(\"span\", \"dot is-\" + dotState);\n    dot.setAttribute(\"aria-hidden\", \"true\");\n    head.appendChild(dot);\n\n    var heading = el(\"h2\", \"account-name\", name);\n    if (isPrimary) {\n      var star = el(\"span\", \"star\", \"★\");\n      star.setAttribute(\"aria-hidden\", \"true\");\n      heading.appendChild(star);\n      heading.appendChild(el(\"span\", \"sr-only\", \" (primary account)\"));\n    }\n    head.appendChild(heading);\n\n    if (typeof account.plan === \"string\" && account.plan) {\n      head.appendChild(el(\"span\", \"chip\", account.plan));\n    }\n    card.appendChild(head);\n\n    var rows = windowsOf(account);\n    if (rows.length === 0) {\n      card.appendChild(el(\"p\", \"card-empty\", \"No usage windows reported.\"));\n      return card;\n    }\n\n    var meters = el(\"div\", \"meters\");\n    rows.forEach(function (source) {\n      if (source && typeof source === \"object\") {\n        meters.appendChild(renderMeterRow(name, source, now));\n      }\n    });\n    card.appendChild(meters);\n    return card;\n  }\n\n  function render() {\n    if (!payload) return;\n\n    var now = Date.now();\n    var generatedAt = parseDate(payload.generatedAt);\n\n    if (generatedAt) {\n      var age = now - generatedAt.getTime();\n      if (age < 0) age = 0;\n      show(updatedEl, age < 60000\n        ? \"Updated just now\"\n        : \"Updated \" + formatDuration(age) + \" ago\");\n\n      if (age > STALE_MS) {\n        show(staleEl, \"The app hasn't reported since \" + formatWhen(generatedAt) + \".\");\n      } else {\n        hide(staleEl);\n      }\n    } else {\n      hide(updatedEl);\n      hide(staleEl);\n    }\n\n    var accounts = orderAccounts(payload);\n    if (accounts.length === 0) {\n      renderMessage(\"No accounts yet\", [\n        \"The app is connected but hasn't reported any accounts.\"\n      ]);\n      return;\n    }\n\n    var list = el(\"div\", \"cards\");\n    accounts.forEach(function (account) {\n      if (!account || typeof account !== \"object\") return;\n      var isPrimary = typeof payload.primary === \"string\" && account.id === payload.primary;\n      list.appendChild(renderCard(account, isPrimary, now));\n    });\n\n    clear(content);\n    content.appendChild(list);\n  }\n\n  // --- data ------------------------------------------------------------\n\n  function load() {\n    if (loading || !id) return;\n    loading = true;\n\n    fetch(API_BASE + \"/u/\" + id, {\n      cache: \"no-store\",\n      headers: { Accept: \"application/json\" }\n    })\n      .then(function (response) {\n        if (response.status === 404) {\n          var missing = new Error(\"not_found\");\n          missing.code = 404;\n          throw missing;\n        }\n        if (!response.ok) throw new Error(\"http_\" + response.status);\n        return response.json();\n      })\n      .then(function (data) {\n        if (!data || typeof data !== \"object\") throw new Error(\"bad_payload\");\n        payload = data;\n        lastFetchAt = Date.now();\n        hide(connectionEl);\n        render();\n      })\n      .catch(function (error) {\n        if (error && error.code === 404) {\n          payload = null;\n          hide(updatedEl);\n          hide(staleEl);\n          hide(connectionEl);\n          renderMessage(\"No data\", [\n            \"The link may have expired (data expires after about a week \" +\n            \"without the app running) or remote view is disabled.\"\n          ]);\n          return;\n        }\n\n        // Network or server hiccup: keep whatever is on screen and say so quietly.\n        if (payload) {\n          show(connectionEl, \"Couldn't refresh just now — retrying shortly.\");\n        } else {\n          renderMessage(\"Can't reach the server\", [\n            \"The usage data couldn't be loaded. This page keeps trying every minute.\",\n            \"If it never loads, check that the remote view address in config.js is correct.\"\n          ]);\n        }\n      })\n      .then(function () {\n        loading = false;\n      });\n  }\n\n  // --- start -----------------------------------------------------------\n\n  function start() {\n    var params = new URLSearchParams(window.location.search);\n    var raw = (params.get(\"id\") || \"\").trim();\n\n    if (!ID_PATTERN.test(raw)) {\n      renderMessage(\"This page shows a shared view of AI usage\", [\n        \"It needs a link that carries an id. Open the app and use \" +\n        \"Settings → Remote view to copy your personal link, then open that link here.\",\n        { before: \"Links look like \", example: \"https://your-site/?id=<32-hex>\", after: \".\" }\n      ]);\n      return;\n    }\n\n    // An empty apiBase is allowed: it means the worker is proxied on this origin.\n    if (typeof CONFIG.apiBase !== \"string\" ||\n        API_BASE.indexOf(\"REPLACE-WITH-YOUR-WORKER-URL\") !== -1) {\n      renderMessage(\"Not configured yet\", [\n        \"This page hasn't been pointed at a remote view address.\",\n        { before: \"Set \", example: \"apiBase\", after: \" in config.js on the server.\" }\n      ]);\n      return;\n    }\n\n    id = raw;\n    renderMessage(\"Loading…\", [\"Fetching the latest usage snapshot.\"]);\n    load();\n\n    window.setInterval(load, REFRESH_MS);\n    window.setInterval(render, TICK_MS);\n\n    document.addEventListener(\"visibilitychange\", function () {\n      if (document.visibilityState !== \"visible\") return;\n      render();\n      if (Date.now() - lastFetchAt >= REFRESH_MS) load();\n    });\n  }\n\n  start();\n})();\n",
  },
  "/config.js": {
    type: "text/javascript; charset=utf-8",
    body: "window.REMOTE_VIEW_CONFIG = { apiBase: \"\" };\n",
  },
};

function asset(entry) {
  return new Response(entry.body, {
    status: 200,
    headers: {
      "Content-Type": entry.type,
      "Cache-Control": STATIC_CACHE,
    },
  });
}

export default {
  async fetch(request, env) {
    if (request.method === "GET") {
      const path = new URL(request.url).pathname;
      const key = path === "/" ? "/index.html" : path;
      if (Object.prototype.hasOwnProperty.call(ASSETS, key)) {
        return asset(ASSETS[key]);
      }
    }
    // /u/{id}, CORS preflight, and every 404/405 stay with the API logic above.
    return api.fetch(request, env);
  },
};
