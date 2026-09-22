const encoder = new TextEncoder();

export function base64UrlEncode(value) {
  const bytes = value instanceof Uint8Array ? value : new Uint8Array(value);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

export function base64UrlDecode(value) {
  if (typeof value !== "string" || !/^[A-Za-z0-9_-]+$/.test(value)) {
    throw new Error("invalid_base64url");
  }
  const padded = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
  const binary = atob(padded);
  return Uint8Array.from(binary, (character) => character.charCodeAt(0));
}

function concat(...parts) {
  const length = parts.reduce((total, part) => total + part.length, 0);
  const result = new Uint8Array(length);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.length;
  }
  return result;
}

async function hmac(keyBytes, value) {
  const key = await crypto.subtle.importKey(
    "raw",
    keyBytes,
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, value));
}

async function hkdfExtract(salt, inputKeyMaterial) {
  return hmac(salt, inputKeyMaterial);
}

async function hkdfExpand(pseudoRandomKey, info, length) {
  const output = [];
  let previous = new Uint8Array();
  let produced = 0;
  for (let counter = 1; produced < length; counter += 1) {
    previous = await hmac(
      pseudoRandomKey,
      concat(previous, info, Uint8Array.of(counter)),
    );
    output.push(previous);
    produced += previous.length;
  }
  return concat(...output).slice(0, length);
}

function vapidJwk(publicKey, privateKey) {
  const publicBytes = base64UrlDecode(publicKey);
  const privateBytes = base64UrlDecode(privateKey);
  if (publicBytes.length !== 65 || publicBytes[0] !== 4 || privateBytes.length !== 32) {
    throw new Error("invalid_vapid_key");
  }
  return {
    kty: "EC",
    crv: "P-256",
    x: base64UrlEncode(publicBytes.slice(1, 33)),
    y: base64UrlEncode(publicBytes.slice(33, 65)),
    d: base64UrlEncode(privateBytes),
  };
}

async function vapidAuthorization(endpoint, configuration, now) {
  const audience = new URL(endpoint).origin;
  const header = base64UrlEncode(encoder.encode(JSON.stringify({ typ: "JWT", alg: "ES256" })));
  const claims = base64UrlEncode(encoder.encode(JSON.stringify({
    aud: audience,
    exp: Math.floor(now / 1000) + 12 * 60 * 60,
    sub: configuration.subject,
  })));
  const unsigned = `${header}.${claims}`;
  const key = await crypto.subtle.importKey(
    "jwk",
    vapidJwk(configuration.publicKey, configuration.privateKey),
    { name: "ECDSA", namedCurve: "P-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign(
    { name: "ECDSA", hash: "SHA-256" },
    key,
    encoder.encode(unsigned),
  );
  return `vapid t=${unsigned}.${base64UrlEncode(signature)}, k=${configuration.publicKey}`;
}

async function encryptPayload(subscription, payload) {
  const userPublic = base64UrlDecode(subscription.keys.p256dh);
  const authSecret = base64UrlDecode(subscription.keys.auth);
  if (userPublic.length !== 65 || userPublic[0] !== 4 || authSecret.length !== 16) {
    throw new Error("invalid_subscription_key");
  }

  const userKey = await crypto.subtle.importKey(
    "raw",
    userPublic,
    { name: "ECDH", namedCurve: "P-256" },
    false,
    [],
  );
  const senderKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  const senderPublic = new Uint8Array(await crypto.subtle.exportKey("raw", senderKeys.publicKey));
  const sharedSecret = new Uint8Array(await crypto.subtle.deriveBits(
    { name: "ECDH", public: userKey },
    senderKeys.privateKey,
    256,
  ));

  const authPrk = await hkdfExtract(authSecret, sharedSecret);
  const inputKeyMaterial = await hkdfExpand(
    authPrk,
    concat(encoder.encode("WebPush: info\0"), userPublic, senderPublic),
    32,
  );
  const salt = crypto.getRandomValues(new Uint8Array(16));
  const pseudoRandomKey = await hkdfExtract(salt, inputKeyMaterial);
  const contentEncryptionKey = await hkdfExpand(
    pseudoRandomKey,
    encoder.encode("Content-Encoding: aes128gcm\0"),
    16,
  );
  const nonce = await hkdfExpand(
    pseudoRandomKey,
    encoder.encode("Content-Encoding: nonce\0"),
    12,
  );
  const plaintext = concat(encoder.encode(payload), Uint8Array.of(2));
  const encryptionKey = await crypto.subtle.importKey(
    "raw",
    contentEncryptionKey,
    "AES-GCM",
    false,
    ["encrypt"],
  );
  const ciphertext = new Uint8Array(await crypto.subtle.encrypt(
    { name: "AES-GCM", iv: nonce, tagLength: 128 },
    encryptionKey,
    plaintext,
  ));
  const recordSize = new Uint8Array(4);
  new DataView(recordSize.buffer).setUint32(0, 4096, false);

  return concat(salt, recordSize, Uint8Array.of(senderPublic.length), senderPublic, ciphertext);
}

// The relay fetches whatever endpoint a browser registers, so the endpoint host
// is a request-forgery surface. Only the real browser push services are
// reachable by default. Entries starting with "*." match any subdomain of that
// domain, at any depth, but never the bare domain itself.
export const DEFAULT_PUSH_ENDPOINT_HOSTS = Object.freeze([
  "fcm.googleapis.com", // Chrome, Chromium, Opera
  "*.push.services.mozilla.com", // Firefox
  "*.push.apple.com", // Safari
  "*.notify.windows.com", // Edge, Windows Notification Service
]);

const IPV4_HOST = /^\d{1,3}(?:\.\d{1,3}){3}$/;

function normalizeHost(value) {
  return value.trim().toLowerCase().replace(/\.$/, "");
}

// Parses a comma-separated operator host list. Returns null when nothing usable
// is configured, so callers can fall back to the defaults.
export function parsePushEndpointHosts(value) {
  if (typeof value !== "string") return null;
  const hosts = value.split(",").map(normalizeHost).filter((host) => host !== "");
  return hosts.length === 0 ? null : hosts;
}

// allowedHosts replaces the default list when it is a non-empty array.
// IP literals are never allowed, even when an operator lists one.
export function isAllowedPushEndpointHost(hostname, allowedHosts = DEFAULT_PUSH_ENDPOINT_HOSTS) {
  if (typeof hostname !== "string") return false;
  const host = normalizeHost(hostname);
  if (host === "" || host.startsWith("[") || IPV4_HOST.test(host)) return false;
  const patterns = Array.isArray(allowedHosts) && allowedHosts.length > 0
    ? allowedHosts
    : DEFAULT_PUSH_ENDPOINT_HOSTS;
  return patterns.some((entry) => {
    if (typeof entry !== "string") return false;
    const pattern = normalizeHost(entry);
    if (pattern === "") return false;
    if (pattern.startsWith("*.")) {
      const suffix = pattern.slice(1);
      return host.length > suffix.length && host.endsWith(suffix);
    }
    return host === pattern;
  });
}

export function validatePushSubscription(subscription, options = {}) {
  try {
    if (subscription === null || typeof subscription !== "object" || Array.isArray(subscription)) return false;
    if (typeof subscription.endpoint !== "string") return false;
    const endpoint = new URL(subscription.endpoint);
    if (endpoint.protocol !== "https:") return false;
    if (subscription.endpoint.length > 2048) return false;
    // No credentials, no alternate port: a push service is reached on 443 only.
    if (endpoint.username !== "" || endpoint.password !== "") return false;
    if (endpoint.port !== "") return false;
    if (!isAllowedPushEndpointHost(endpoint.hostname, options?.allowedEndpointHosts)) return false;
    const p256dh = base64UrlDecode(subscription.keys?.p256dh);
    const auth = base64UrlDecode(subscription.keys?.auth);
    return p256dh.length === 65 && p256dh[0] === 4 && auth.length === 16;
  } catch {
    return false;
  }
}

export function validateVapidConfiguration(configuration) {
  try {
    if (!configuration || typeof configuration.subject !== "string") return false;
    if (!/^(mailto:|https:)/.test(configuration.subject)) return false;
    vapidJwk(configuration.publicKey, configuration.privateKey);
    return true;
  } catch {
    return false;
  }
}

export async function sendWebPush(
  subscription,
  message,
  configuration,
  fetchImplementation = fetch,
  now = Date.now(),
  options = {},
) {
  if (!validatePushSubscription(subscription, options)) throw new Error("invalid_subscription");
  if (!validateVapidConfiguration(configuration)) throw new Error("invalid_vapid_configuration");

  const payload = typeof message === "string" ? message : JSON.stringify(message);
  const encrypted = await encryptPayload(subscription, payload);
  const authorization = await vapidAuthorization(subscription.endpoint, configuration, now);
  return fetchImplementation(subscription.endpoint, {
    method: "POST",
    // A redirect would take the request off the validated host, and a hung push
    // service must not hold the delivery open.
    redirect: "error",
    signal: AbortSignal.timeout(10_000),
    headers: {
      Authorization: authorization,
      "Content-Encoding": "aes128gcm",
      "Content-Type": "application/octet-stream",
      TTL: "300",
      Urgency: "high",
    },
    body: encrypted,
  });
}
