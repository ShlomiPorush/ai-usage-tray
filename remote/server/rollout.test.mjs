import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { appendFileSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { mkdtemp, rm } from "node:fs/promises";
import { request as httpRequest } from "node:http";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterEach, beforeEach, test } from "node:test";
import { fileURLToPath } from "node:url";
import vm from "node:vm";
import { createRemoteViewServer, deriveReadId, SnapshotStore } from "./server.mjs";

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = join(HERE, "..", "..", "web");
const SHELL_FILES = ["index.html", "styles.css", "usage.js", "app.js", "config.js", "icon-192.png", "icon-512.png"];

let directory;
let servers;

function releaseWebRoot(name, { inlineScript = "" } = {}) {
  const root = join(directory, `web-${name}`);
  cpSync(WEB_ROOT, root, { recursive: true });
  for (const file of ["styles.css", "usage.js", "app.js"]) {
    appendFileSync(join(root, file), `\n/* release ${name} */\n`);
  }
  for (const file of ["icon-192.png", "icon-512.png"]) {
    appendFileSync(join(root, file), Buffer.from(`release ${name}`));
  }
  const index = readFileSync(join(root, "index.html"), "utf8")
    .replace('<script src="app.js">', `<script src='app.js'>`)
    .replace('href="styles.css"', 'href="./styles.css"')
    .replace("<title>AI Usage Tray</title>", `<title>AI Usage Tray ${name}</title>`)
    .replace("<script>\n  // Applied before first paint", `<script>${inlineScript}\n  // Applied before first paint`);
  writeFileSync(join(root, "index.html"), index);
  return root;
}

async function startServer(webRoot) {
  const app = createRemoteViewServer({
    databasePath: join(directory, "data", "usage.db"),
    webRoot,
    cleanupIntervalMs: 0,
    runtimeVersion: "test",
  });
  await new Promise((resolveListen) => app.server.listen(0, "127.0.0.1", resolveListen));
  const server = { app, baseUrl: `http://127.0.0.1:${app.server.address().port}` };
  servers.push(server);
  return server;
}

async function body(response) {
  return Buffer.from(await response.arrayBuffer());
}

function localReferences(html) {
  const references = [];
  for (const tag of html.matchAll(/<(?:script|link)\b[^>]*>/gi)) {
    const reference = tag[0].match(/(?:src|href)=["']([^"']+)["']/i)?.[1];
    if (reference) references.push(reference);
  }
  return references;
}

function rawGet(baseUrl, path) {
  const { hostname, port } = new URL(baseUrl);
  return new Promise((resolveRequest, rejectRequest) => {
    const request = httpRequest({ hostname, port, path, method: "GET" }, (response) => {
      response.resume();
      response.on("end", () => resolveRequest(response.statusCode));
    });
    request.on("error", rejectRequest);
    request.end();
  });
}

class FakeCache {
  constructor(fetchImpl) {
    this.entries = new Map();
    this.fetchImpl = fetchImpl;
  }

  async addAll(requests) {
    const responses = await Promise.all(requests.map((request) => this.fetchImpl(request)));
    for (const response of responses) {
      if (!response.ok) throw new TypeError(`addAll failed: ${response.status}`);
    }
    for (let index = 0; index < requests.length; index += 1) {
      this.entries.set(new URL(requests[index].url).pathname, responses[index]);
    }
  }

  async put(request, response) {
    this.entries.set(new URL(request.url).pathname, response);
  }

  async match(request) {
    const response = this.entries.get(new URL(typeof request === "string" ? request : request.url).pathname);
    return response ? response.clone() : undefined;
  }
}

function serviceWorkerSandbox(origin, upstreams, { failPath = null } = {}) {
  let turn = 0;
  const fetched = [];
  const fetchImpl = async (input) => {
    const url = new URL(typeof input === "string" ? input : input.url);
    const upstream = upstreams[turn % upstreams.length];
    turn += 1;
    fetched.push({ path: url.pathname, upstream: upstream.baseUrl });
    if (url.pathname === failPath) return new Response("missing", { status: 503 });
    return fetch(upstream.baseUrl + url.pathname + url.search);
  };
  const cacheStorage = new Map();
  const caches = {
    async open(name) {
      if (!cacheStorage.has(name)) cacheStorage.set(name, new FakeCache(fetchImpl));
      return cacheStorage.get(name);
    },
    async keys() {
      return [...cacheStorage.keys()];
    },
    async delete(name) {
      return cacheStorage.delete(name);
    },
    async match(request) {
      for (const cache of cacheStorage.values()) {
        const hit = await cache.match(request);
        if (hit) return hit;
      }
      return undefined;
    },
  };
  const listeners = new Map();
  const self = {
    location: new URL("/sw.js", origin),
    registration: { scope: `${origin}/` },
    addEventListener(type, listener) {
      listeners.set(type, listener);
    },
    skipWaiting: async () => {},
    clients: { claim: async () => {} },
  };
  class SandboxRequest {
    constructor(input) {
      this.url = new URL(typeof input === "string" ? input : input.url, self.location).href;
      this.method = "GET";
    }
  }
  const context = vm.createContext({
    self,
    caches,
    fetch: fetchImpl,
    Request: SandboxRequest,
    Response,
    URL,
    URLSearchParams,
    Promise,
    clients: self.clients,
  });

  return {
    cacheStorage,
    fetched,
    run(source) {
      vm.runInContext(source, context);
    },
    async install() {
      let pending = Promise.resolve();
      listeners.get("install")({ waitUntil(promise) { pending = promise; } });
      await pending;
    },
    async activate() {
      let pending = Promise.resolve();
      listeners.get("activate")({ waitUntil(promise) { pending = promise; } });
      await pending;
    },
    async request(path) {
      let answer = null;
      listeners.get("fetch")({
        request: { url: `${origin}${path}`, method: "GET" },
        respondWith(response) { answer = response; },
      });
      return answer === null ? null : await answer;
    },
  };
}

beforeEach(async () => {
  directory = await mkdtemp(join(tmpdir(), "ai-usage-rollout-"));
  servers = [];
});

afterEach(async () => {
  for (const server of servers) await server.app.close();
  await rm(directory, { recursive: true, force: true });
});

test("a page from one release loads only that release's files from either container", async () => {
  const releaseA = await startServer(releaseWebRoot("a"));
  const releaseB = await startServer(releaseWebRoot("b", { inlineScript: "\n  window.releaseB = true;" }));

  const index = await fetch(`${releaseA.baseUrl}/`);
  assert.equal(index.status, 200);
  const html = await index.text();
  assert.match(html, /<script src='shell\/[0-9a-f]{16}\/app\.js'>/);
  assert.doesNotMatch(html, /\b(?:src|href)\s*=\s*["']?(?:\.\/)?(?:styles\.css|config\.js|usage\.js|app\.js|icon-192\.png)/);
  const references = localReferences(html).filter((reference) => !reference.startsWith("manifest.webmanifest"));
  assert.ok(references.length >= 4);

  for (const reference of references) {
    const fromA = await fetch(new URL(reference, `${releaseA.baseUrl}/`));
    const fromB = await fetch(new URL(reference, `${releaseB.baseUrl}/`));
    assert.equal(fromB.status, 200, reference);
    assert.equal(fromB.headers.get("content-type"), fromA.headers.get("content-type"), reference);
    assert.deepEqual(await body(fromB), await body(fromA), reference);
  }

  assert.equal(index.headers.get("cache-control"), "no-cache, no-transform");
  for (const reference of references) {
    assert.match(reference, /^shell\/[0-9a-f]{16}\/[a-z0-9-]+\.(?:css|js|png)$/);
  }
  const shellId = references[0].split("/")[1];
  const indexFromA = await fetch(`${releaseA.baseUrl}/shell/${shellId}/index.html`);
  const indexFromB = await fetch(`${releaseB.baseUrl}/shell/${shellId}/index.html`);
  assert.equal(indexFromB.status, 200);
  assert.equal(indexFromB.headers.get("content-security-policy"), indexFromA.headers.get("content-security-policy"));
  assert.equal(indexFromB.headers.get("content-security-policy"), index.headers.get("content-security-policy"));
  assert.notEqual(
    (await fetch(`${releaseB.baseUrl}/`)).headers.get("content-security-policy"),
    index.headers.get("content-security-policy"),
  );
  assert.deepEqual(await body(indexFromB), await body(indexFromA));
  assert.equal(indexFromB.headers.get("cache-control"), "public, max-age=31536000, immutable, no-transform");
  const scriptFromB = await fetch(new URL(references.find((reference) => reference.endsWith("app.js")), `${releaseB.baseUrl}/`));
  assert.equal(scriptFromB.headers.get("cache-control"), "public, max-age=31536000, immutable");
});

for (const [installer, other] of [["a", "b"], ["b", "a"]]) {
  test(`a service worker installed from release ${installer} caches only that release`, async () => {
    const releases = {
      a: await startServer(releaseWebRoot("a")),
      b: await startServer(releaseWebRoot("b", { inlineScript: "\n  window.releaseB = true;" })),
    };
    const origin = releases[installer].baseUrl;
    const sandbox = serviceWorkerSandbox(origin, [releases[other], releases[installer]]);
    sandbox.run(await (await fetch(`${origin}/sw.js`)).text());
    await sandbox.install();
    await sandbox.activate();

    assert.equal(sandbox.cacheStorage.size, 1);
    const [cache] = sandbox.cacheStorage.values();
    assert.ok(cache.entries.size >= SHELL_FILES.length);
    assert.ok(new Set(sandbox.fetched.map((entry) => entry.upstream)).size > 1);
    for (const [path, response] of cache.entries) {
      const own = await fetch(releases[installer].baseUrl + path);
      assert.deepEqual(await body(response.clone()), await body(own), path);
      assert.equal(response.headers.get("content-security-policy"), own.headers.get("content-security-policy"), path);
    }

    const navigation = await sandbox.request("/?id=0123456789abcdef0123456789abcdef");
    assert.ok(navigation);
    const ownIndex = await fetch(`${releases[installer].baseUrl}/`);
    assert.deepEqual(await body(navigation), await body(ownIndex));
  });
}

test("a service worker leaves another release's files to the network, which serves them", async () => {
  const releaseA = await startServer(releaseWebRoot("a"));
  const releaseB = await startServer(releaseWebRoot("b"));
  const shellA = localReferences(await (await fetch(`${releaseA.baseUrl}/`)).text())
    .find((reference) => reference.endsWith("app.js"));
  const sandbox = serviceWorkerSandbox(releaseB.baseUrl, [releaseB]);
  sandbox.run(await (await fetch(`${releaseB.baseUrl}/sw.js`)).text());
  await sandbox.install();

  assert.equal(await sandbox.request(`/${shellA}`), null);
  const fromB = await fetch(`${releaseB.baseUrl}/${shellA}`);
  assert.equal(fromB.status, 200);
  assert.deepEqual(await body(fromB), readFileSync(join(directory, "web-a", "app.js")));
});

test("a service worker install fails when one shell file fails", async () => {
  const release = await startServer(releaseWebRoot("a"));
  const shellPath = new URL(
    localReferences(await (await fetch(`${release.baseUrl}/`)).text()).find((reference) => reference.endsWith("usage.js")),
    `${release.baseUrl}/`,
  ).pathname;
  const sandbox = serviceWorkerSandbox(release.baseUrl, [release], { failPath: shellPath });
  sandbox.run(await (await fetch(`${release.baseUrl}/sw.js`)).text());
  await assert.rejects(sandbox.install());
});

test("the shell store reuses a published release and serves only its files", async () => {
  const webRoot = releaseWebRoot("a");
  const first = await startServer(webRoot);
  await startServer(webRoot);
  const shellRoot = join(directory, "data", "shell");
  const published = readdirSync(shellRoot).filter((name) => !name.startsWith("."));
  assert.equal(published.length, 1);
  const [shellId] = published;
  assert.ok(existsSync(join(shellRoot, shellId, "shell.json")));
  assert.deepEqual(readdirSync(shellRoot).filter((name) => name.startsWith(".")), []);

  const missingId = "0123456789abcdef" === shellId ? "fedcba9876543210" : "0123456789abcdef";
  for (const path of [
    `/shell/${missingId}/app.js`,
    `/shell/${shellId}/missing.js`,
    `/shell/${shellId}/shell.json`,
    `/shell/${shellId}/App.js`,
    `/shell/${shellId}`,
    "/shell/",
  ]) {
    const response = await fetch(first.baseUrl + path);
    assert.equal(response.status, 404, path);
    assert.equal(response.headers.get("cache-control"), "no-store", path);
  }
  for (const path of [
    `/shell/${shellId}/../../data/usage.db`,
    `/shell/${shellId}/%2e%2e/%2e%2e/usage.db`,
    `/shell/${shellId}/..%2fshell.json`,
    `/shell/${shellId}/%2e%2e%2f${shellId}%2fshell.json`,
  ]) {
    assert.equal(await rawGet(first.baseUrl, path), 404, path);
  }

  mkdirSync(join(shellRoot, "0000000000000000"));
  writeFileSync(join(shellRoot, "0000000000000000", "shell.json"), JSON.stringify({ csp: "default-src 'none'" }));
  writeFileSync(join(shellRoot, "0000000000000000", "extra-file.js"), "window.extra = true;\n");
  const extra = await fetch(`${first.baseUrl}/shell/0000000000000000/extra-file.js`);
  assert.equal(extra.status, 200);
  assert.equal(extra.headers.get("content-type"), "text/javascript; charset=utf-8");
  assert.equal(await extra.text(), "window.extra = true;\n");
});

test("a server whose shell cannot be published does not start", async () => {
  mkdirSync(join(directory, "data"), { recursive: true });
  writeFileSync(join(directory, "data", "shell"), "not a directory");
  assert.throws(() => createRemoteViewServer({
    databasePath: join(directory, "data", "usage.db"),
    webRoot: WEB_ROOT,
    cleanupIntervalMs: 0,
  }));
});

test("the database schema equals the snapshot of the release named in ROLLOUT_FROM", () => {
  const rolloutFrom = readFileSync(join(HERE, "ROLLOUT_FROM"), "utf8").trim();
  const snapshotPath = join(HERE, "rollout-schema", `${rolloutFrom}.json`);
  assert.ok(existsSync(snapshotPath), `remote/server/rollout-schema/${rolloutFrom}.json is missing`);
  const store = new SnapshotStore(join(directory, "schema.db"));
  try {
    assert.deepEqual(
      store.schema(),
      JSON.parse(readFileSync(snapshotPath, "utf8")),
      "The schema changed. Add remote/server/rollout-schema/<VERSION>.json and set ROLLOUT_FROM to VERSION.",
    );
  } finally {
    store.close();
  }
});

test("writes through two containers on one database are all readable from both", async () => {
  const releaseA = await startServer(releaseWebRoot("a"));
  const releaseB = await startServer(releaseWebRoot("b"));
  const snapshot = {
    version: 2,
    generatedAt: "2026-11-26T12:00:00Z",
    primary: "claude:test",
    accounts: [],
  };
  const writeIds = Array.from({ length: 6 }, () => randomBytes(16).toString("hex"));
  for (const [index, writeId] of writeIds.entries()) {
    const target = index % 2 === 0 ? releaseA : releaseB;
    const response = await fetch(`${target.baseUrl}/u/${writeId}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(snapshot),
    });
    assert.equal(response.status, 204);
  }
  for (const writeId of writeIds) {
    for (const server of [releaseA, releaseB]) {
      assert.equal((await fetch(`${server.baseUrl}/u/${deriveReadId(writeId)}`)).status, 200);
    }
  }
});
