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

import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const webDir = join(here, "..", "..", "web");

const read = (path) => readFileSync(path, "utf8");
const readBase64 = (path) => readFileSync(path).toString("base64");

// --- API section -------------------------------------------------------------
// worker.js is reused verbatim (minus its `export default`) so the bundled API
// behaviour can never drift from the standalone one.
const workerSource = read(join(here, "worker.js"));
const EXPORT_MARKER = "export default {";
const markerCount = workerSource.split(EXPORT_MARKER).length - 1;
if (markerCount !== 1) {
  throw new Error(
    `worker.js must contain exactly one "${EXPORT_MARKER}" (found ${markerCount}); ` +
      "update bundle.mjs to match.",
  );
}
const apiSection = workerSource.replace(EXPORT_MARKER, "const api = {").trimEnd();

// --- Static assets -----------------------------------------------------------
const CONFIG_BODY = 'window.REMOTE_VIEW_CONFIG = { apiBase: "" };\n';

// The service worker must never be cached, or an old shell version would keep
// re-installing itself long after the worker was redeployed.
const NO_STORE = "no-store";

const assets = [
  { path: "/index.html", type: "text/html; charset=utf-8", text: read(join(webDir, "index.html")) },
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
// Serves the JSON API (PUT/GET /u/{id}) and the viewer page from a single URL.
// Requires one KV binding named USAGE.

${apiSection}

const STATIC_CACHE = "public, max-age=300";

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
  return new Response(entry.base64 === undefined ? entry.body : decode(entry), {
    status: 200,
    headers: {
      "Content-Type": entry.type,
      "Cache-Control": entry.cache || STATIC_CACHE,
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
`;

const outPath = join(here, "worker.bundle.js");
writeFileSync(outPath, output);
const kb = (Buffer.byteLength(output) / 1024).toFixed(1);
console.log(`wrote worker.bundle.js (${kb} KB)`);
