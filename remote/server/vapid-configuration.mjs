import { chmod, mkdir, readFile, writeFile } from "node:fs/promises";
import { dirname } from "node:path";
import { base64UrlEncode, validateVapidConfiguration } from "../shared/web-push.mjs";

const DEFAULT_SUBJECT = "https://github.com/ShlomiPorush/ai-usage-tray";

function environmentConfiguration(env) {
  const values = [env.VAPID_PUBLIC_KEY, env.VAPID_PRIVATE_KEY, env.VAPID_SUBJECT];
  if (values.every((value) => value === undefined)) return null;

  const configuration = {
    publicKey: env.VAPID_PUBLIC_KEY,
    privateKey: env.VAPID_PRIVATE_KEY,
    subject: env.VAPID_SUBJECT,
  };
  if (!validateVapidConfiguration(configuration)) {
    throw new Error(
      "VAPID_PUBLIC_KEY, VAPID_PRIVATE_KEY and VAPID_SUBJECT must form a valid configuration",
    );
  }
  return configuration;
}

async function generateConfiguration() {
  const keys = await crypto.subtle.generateKey(
    { name: "ECDSA", namedCurve: "P-256" },
    true,
    ["sign", "verify"],
  );
  const privateKey = await crypto.subtle.exportKey("jwk", keys.privateKey);
  return {
    publicKey: base64UrlEncode(await crypto.subtle.exportKey("raw", keys.publicKey)),
    privateKey: privateKey.d,
    subject: DEFAULT_SUBJECT,
  };
}

async function readPersistedConfiguration(path) {
  const configuration = JSON.parse(await readFile(path, "utf8"));
  if (!validateVapidConfiguration(configuration)) {
    throw new Error(`Persisted VAPID configuration is invalid: ${path}`);
  }
  await chmod(path, 0o600);
  return configuration;
}

export async function ensureVapidConfiguration({ env = process.env, path } = {}) {
  const configured = environmentConfiguration(env);
  if (configured !== null) return configured;

  if (!path) return generateConfiguration();

  try {
    return await readPersistedConfiguration(path);
  } catch (error) {
    if (error?.code !== "ENOENT") throw error;
  }

  const generated = await generateConfiguration();
  await mkdir(dirname(path), { recursive: true });
  try {
    await writeFile(path, `${JSON.stringify(generated, null, 2)}\n`, {
      encoding: "utf8",
      flag: "wx",
      mode: 0o600,
    });
    return generated;
  } catch (error) {
    if (error?.code !== "EEXIST") throw error;
    return readPersistedConfiguration(path);
  }
}
