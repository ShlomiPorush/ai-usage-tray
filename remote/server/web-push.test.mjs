import assert from "node:assert/strict";
import { test } from "node:test";
import {
  base64UrlEncode,
  sendWebPush,
} from "../shared/web-push.mjs";

const encoder = new TextEncoder();

function concat(...parts) {
  const result = new Uint8Array(parts.reduce((total, part) => total + part.length, 0));
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

async function expand(key, info, length) {
  return (await hmac(key, concat(info, Uint8Array.of(1)))).slice(0, length);
}

test("encrypts a payload that the browser subscription key can decrypt", async () => {
  const subscriberKeys = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" },
    true,
    ["deriveBits"],
  );
  const subscriberPublic = new Uint8Array(
    await crypto.subtle.exportKey("raw", subscriberKeys.publicKey),
  );
  const authSecret = crypto.getRandomValues(new Uint8Array(16));
  const subscription = {
    endpoint: "https://push.example.test/message/123",
    keys: {
      p256dh: base64UrlEncode(subscriberPublic),
      auth: base64UrlEncode(authSecret),
    },
  };
  const vapidKeys = await crypto.subtle.generateKey(
    { name: "ECDSA", namedCurve: "P-256" },
    true,
    ["sign", "verify"],
  );
  const vapidPrivate = await crypto.subtle.exportKey("jwk", vapidKeys.privateKey);
  const configuration = {
    publicKey: base64UrlEncode(await crypto.subtle.exportKey("raw", vapidKeys.publicKey)),
    privateKey: vapidPrivate.d,
    subject: "mailto:test@example.com",
  };
  const message = { type: "usage-alerts", alerts: [{ accountId: "claude:work" }] };
  let request;

  const response = await sendWebPush(
    subscription,
    message,
    configuration,
    async (url, options) => {
      request = { url, options };
      return new Response(null, { status: 201 });
    },
    Date.parse("2026-08-28T12:00:00Z"),
  );

  assert.equal(response.status, 201);
  assert.equal(request.url, subscription.endpoint);
  assert.equal(request.options.headers["Content-Encoding"], "aes128gcm");
  assert.match(request.options.headers.Authorization, /^vapid t=[^.]+\.[^.]+\.[^,]+, k=/);

  const body = request.options.body;
  const salt = body.slice(0, 16);
  assert.equal(new DataView(body.buffer, body.byteOffset + 16, 4).getUint32(0, false), 4096);
  assert.equal(body[20], 65);
  const senderPublic = body.slice(21, 86);
  const ciphertext = body.slice(86);
  const senderKey = await crypto.subtle.importKey(
    "raw",
    senderPublic,
    { name: "ECDH", namedCurve: "P-256" },
    false,
    [],
  );
  const sharedSecret = new Uint8Array(await crypto.subtle.deriveBits(
    { name: "ECDH", public: senderKey },
    subscriberKeys.privateKey,
    256,
  ));
  const authPrk = await hmac(authSecret, sharedSecret);
  const inputKeyMaterial = await expand(
    authPrk,
    concat(encoder.encode("WebPush: info\0"), subscriberPublic, senderPublic),
    32,
  );
  const pseudoRandomKey = await hmac(salt, inputKeyMaterial);
  const contentEncryptionKey = await expand(
    pseudoRandomKey,
    encoder.encode("Content-Encoding: aes128gcm\0"),
    16,
  );
  const nonce = await expand(
    pseudoRandomKey,
    encoder.encode("Content-Encoding: nonce\0"),
    12,
  );
  const key = await crypto.subtle.importKey("raw", contentEncryptionKey, "AES-GCM", false, ["decrypt"]);
  const plaintext = new Uint8Array(await crypto.subtle.decrypt(
    { name: "AES-GCM", iv: nonce, tagLength: 128 },
    key,
    ciphertext,
  ));

  assert.equal(plaintext.at(-1), 2);
  assert.deepEqual(JSON.parse(new TextDecoder().decode(plaintext.slice(0, -1))), message);
});
