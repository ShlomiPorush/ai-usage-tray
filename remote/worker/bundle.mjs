// Builds worker.bundle.js: one file that serves both the remote-view API and the
// viewer page, so it can be pasted straight into the Cloudflare dashboard editor.
//
//   cd remote/worker && node bundle.mjs
//
// Inputs:  worker.js (API logic), ../../web/index.html, styles.css, app.js
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

const assets = [
  ["/index.html", "text/html; charset=utf-8", read(join(webDir, "index.html"))],
  ["/styles.css", "text/css; charset=utf-8", read(join(webDir, "styles.css"))],
  ["/app.js", "text/javascript; charset=utf-8", read(join(webDir, "app.js"))],
  ["/config.js", "text/javascript; charset=utf-8", CONFIG_BODY],
];

// JSON.stringify keeps backticks, quotes, newlines and non-ASCII text from ever
// breaking out of the generated source.
const assetEntries = assets
  .map(
    ([path, type, body]) =>
      `  ${JSON.stringify(path)}: {\n` +
      `    type: ${JSON.stringify(type)},\n` +
      `    body: ${JSON.stringify(body)},\n` +
      `  },`,
  )
  .join("\n");

// --- Template ----------------------------------------------------------------
const output = `// AI Usage Tray - remote view worker, bundled.
//
// GENERATED FILE - do not edit by hand.
// Re-create it with:  cd remote/worker && node bundle.mjs
// Sources: worker.js, web/index.html, web/styles.css, web/app.js
//
// Serves the JSON API (PUT/GET /u/{id}) and the viewer page from a single URL.
// Requires one KV binding named USAGE.

${apiSection}

const STATIC_CACHE = "public, max-age=300";

const ASSETS = {
${assetEntries}
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
`;

const outPath = join(here, "worker.bundle.js");
writeFileSync(outPath, output);
const kb = (Buffer.byteLength(output) / 1024).toFixed(1);
console.log(`wrote worker.bundle.js (${kb} KB)`);
