// Refreshes the bundled price snapshot from the LiteLLM model-prices catalog.
//
// Usage: node scripts/update-pricing-snapshot.mjs [catalog-url-or-file]
//
// The snapshot is what the app prices with when it has never reached the live
// catalog (first start offline, or the download is disabled). It keeps the
// catalog's own shape, trimmed to first-party Anthropic and OpenAI chat models
// and to the cost fields the app reads, so the app parses both with one parser.
//
// Entries are merged, never dropped: a model that disappears from the catalog
// (LiteLLM prunes retired models) keeps its last known price here, because old
// session logs still contain it. Run this before every release and commit the
// diff.

import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const DEFAULT_SOURCE = 'https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json';
const PROVIDERS = new Set(['anthropic', 'openai']);
const MODES = new Set(['chat', 'responses']);
const FIELDS = [
  'litellm_provider',
  'mode',
  'input_cost_per_token',
  'output_cost_per_token',
  'cache_read_input_token_cost',
  'cache_creation_input_token_cost',
  'cache_creation_input_token_cost_above_1hr'
];

const here = path.dirname(fileURLToPath(import.meta.url));
const snapshotPath = path.resolve(here, '..', 'src', 'costats.Core', 'Analytics', 'pricing-snapshot.json');

async function loadCatalog(source) {
  if (/^https?:\/\//i.test(source)) {
    const response = await fetch(source, { headers: { 'User-Agent': 'AIUsageTray-pricing-snapshot' } });
    if (!response.ok) {
      throw new Error(`Catalog download failed: HTTP ${response.status}`);
    }
    return response.json();
  }
  return JSON.parse(await readFile(source, 'utf8'));
}

function trim(catalog) {
  const models = {};
  for (const [id, entry] of Object.entries(catalog)) {
    if (!entry || typeof entry !== 'object' ||
        !PROVIDERS.has(entry.litellm_provider) || !MODES.has(entry.mode) ||
        typeof entry.input_cost_per_token !== 'number' || typeof entry.output_cost_per_token !== 'number') {
      continue;
    }
    const kept = {};
    for (const field of FIELDS) {
      if (entry[field] !== undefined && entry[field] !== null) {
        kept[field] = entry[field];
      }
    }
    models[id] = kept;
  }
  return models;
}

async function loadExisting() {
  try {
    return JSON.parse(await readFile(snapshotPath, 'utf8'));
  } catch (error) {
    if (error.code === 'ENOENT') {
      return {};
    }
    throw error;
  }
}

const source = process.argv[2] ?? DEFAULT_SOURCE;
const fresh = trim(await loadCatalog(source));
if (Object.keys(fresh).length < 20) {
  throw new Error(`Catalog looks wrong: only ${Object.keys(fresh).length} usable entries.`);
}

const existing = await loadExisting();
const merged = { ...existing, ...fresh };
const sorted = Object.fromEntries(Object.keys(merged).sort().map(id => [id, merged[id]]));

const added = Object.keys(fresh).filter(id => !(id in existing));
const changed = Object.keys(fresh).filter(id => id in existing && JSON.stringify(existing[id]) !== JSON.stringify(fresh[id]));
const retained = Object.keys(existing).filter(id => !(id in fresh));

await writeFile(snapshotPath, JSON.stringify(sorted, null, 2) + '\n', 'utf8');

console.log(`Snapshot: ${Object.keys(sorted).length} models (${added.length} added, ${changed.length} changed, ${retained.length} kept from the previous snapshot).`);
for (const id of added) console.log(`  + ${id}`);
for (const id of changed) console.log(`  ~ ${id}`);
