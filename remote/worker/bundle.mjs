// Builds worker.bundle.js: one file that serves both the remote-view API and the
// viewer page, so it can be pasted straight into the Cloudflare dashboard editor.
//
//   cd remote/worker && node bundle.mjs
//
// Inputs:  worker.js (API logic) and the whole of ../../web/: the page, its
//          stylesheet and script, the PWA manifest, the service worker and the
//          two icons (embedded as base64).
// Output:  worker.bundle.js
//
// web/config.js is deliberately NOT bundled: the bundled worker serves the page and
// the API from the same origin, so it emits an empty apiBase instead.

import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const webDir = join(here, "..", "..", "web");

const read = (path) => readFileSync(path, "utf8");
const readBase64 = (path) => readFileSync(path).toString("base64");

// --- API section -------------------------------------------------------------
// Shared Web Push and threshold logic is inlined before worker.js. Imports and
// named exports are removed because the dashboard bundle must remain one file.
const sharedSource = [
  read(join(here, "..", "shared", "usage-alerts.mjs")),
  read(join(here, "..", "shared", "web-push.mjs")),
].join("\n\n").replace(/^export /gm, "");
const workerSource = read(join(here, "worker.js")).replace(
  /^import[\s\S]*?from "\.\.\/shared\/web-push\.mjs";\r?\n/m,
  "",
).replace(
  /^import \{ findResetAlerts, findThresholdCrossings \} from "\.\.\/shared\/usage-alerts\.mjs";\r?\n/m,
  "",
);
const EXPORT_MARKER = "export default {";
const markerCount = workerSource.split(EXPORT_MARKER).length - 1;
if (markerCount !== 1) {
  throw new Error(
    `worker.js must contain exactly one "${EXPORT_MARKER}" (found ${markerCount}); ` +
      "update bundle.mjs to match.",
  );
}
const apiSection = `${sharedSource}\n\n${workerSource.replace(EXPORT_MARKER, "const api = {")}`.trimEnd();

// --- Static assets -----------------------------------------------------------
const CONFIG_BODY = 'window.REMOTE_VIEW_CONFIG = { apiBase: "" };\n';

// The service worker must never be cached, or an old shell version would keep
// re-installing itself long after the worker was redeployed.
const NO_STORE = "no-store";

const indexHtml = read(join(webDir, "index.html"));

// --- Content-Security-Policy -------------------------------------------------
// The page carries one inline <script> (the pre-paint theme guard). Rather than
// weaken script-src with 'unsafe-inline', every inline script is hashed here at
// build time, so the policy stays exact and breaks loudly if the page changes
// without a rebuild.
const INLINE_SCRIPT_RE = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;
const scriptHashes = [...indexHtml.matchAll(INLINE_SCRIPT_RE)].map(
  (m) => `'sha256-${createHash("sha256").update(m[1], "utf8").digest("base64")}'`,
);

// default-src 'none' plus one allowance per resource the page actually uses.
// connect-src 'self' is enough because the bundled config.js leaves apiBase
// empty: page and API share an origin.
const CSP = [
  "default-src 'none'",
  "base-uri 'none'",
  `script-src 'self'${scriptHashes.length ? " " + scriptHashes.join(" ") : ""}`,
  // No inline style attributes in the markup; app.js only touches CSSOM
  // properties, which CSP does not govern.
  "style-src 'self'",
  "img-src 'self'",
  "connect-src 'self'",
  "manifest-src 'self'",
  "worker-src 'self'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join("; ");

const assets = [
  { path: "/index.html", type: "text/html; charset=utf-8", text: indexHtml, html: true },
  { path: "/styles.css", type: "text/css; charset=utf-8", text: read(join(webDir, "styles.css")) },
  { path: "/app.js", type: "text/javascript; charset=utf-8", text: read(join(webDir, "app.js")) },
  { path: "/config.js", type: "text/javascript; charset=utf-8", text: CONFIG_BODY },
  {
    path: "/manifest.webmanifest",
    type: "application/manifest+json; charset=utf-8",
    text: read(join(webDir, "manifest.webmanifest")),
  },
  {
    path: "/sw.js",
    type: "text/javascript; charset=utf-8",
    text: read(join(webDir, "sw.js")),
    cache: NO_STORE,
  },
  { path: "/icon-192.png", type: "image/png", base64: readBase64(join(webDir, "icon-192.png")) },
  { path: "/icon-512.png", type: "image/png", base64: readBase64(join(webDir, "icon-512.png")) },
];

// JSON.stringify keeps backticks, quotes, newlines and non-ASCII text from ever
// breaking out of the generated source.
const assetEntries = assets
  .map((entry) => {
    const fields = [`    type: ${JSON.stringify(entry.type)},`];
    if (entry.html) fields.push("    html: true,");
    if (entry.cache) fields.push(`    cache: ${JSON.stringify(entry.cache)},`);
    fields.push(
      entry.base64 === undefined
        ? `    body: ${JSON.stringify(entry.text)},`
        : `    base64: ${JSON.stringify(entry.base64)},`,
    );
    return `  ${JSON.stringify(entry.path)}: {\n${fields.join("\n")}\n  },`;
  })
  .join("\n");

// --- Template ----------------------------------------------------------------
const output = `// AI Usage Tray - remote view worker, bundled.
//
// GENERATED FILE - do not edit by hand.
// Re-create it with:  cd remote/worker && node bundle.mjs
// Sources: worker.js and every file in web/ (page, styles, script, manifest,
//          service worker, icons).
//
// Serves the JSON API (PUT/DELETE /u/{writeId}, GET /u/{readId}) and the viewer
// page from a single URL.
// Requires one KV binding named USAGE.

${apiSection}

const STATIC_CACHE = "public, max-age=300";

// Only the page needs these; SECURITY (nosniff, no-referrer) comes from the
// API section above and is applied to every response.
const HTML_HEADERS = {
  "Content-Security-Policy": ${JSON.stringify(CSP)},
  "X-Frame-Options": "DENY",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=()",
};

const ASSETS = {
${assetEntries}
};

// Binary assets travel as base64 and are decoded once, on first request.
function decode(entry) {
  if (!entry.bytes) {
    const binary = atob(entry.base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    entry.bytes = bytes;
  }
  return entry.bytes;
}

function asset(entry) {
  const headers = {
    ...SECURITY,
    "Content-Type": entry.type,
    "Cache-Control": entry.cache || STATIC_CACHE,
  };
  if (entry.html) Object.assign(headers, HTML_HEADERS);
  return new Response(entry.base64 === undefined ? entry.body : decode(entry), {
    status: 200,
    headers,
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
`;

const outPath = join(here, "worker.bundle.js");
writeFileSync(outPath, output);
const kb = (Buffer.byteLength(output) / 1024).toFixed(1);
console.log(`wrote worker.bundle.js (${kb} KB)`);
