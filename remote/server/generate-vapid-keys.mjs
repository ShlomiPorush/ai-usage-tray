import { base64UrlEncode } from "../shared/web-push.mjs";

const keys = await crypto.subtle.generateKey(
  { name: "ECDSA", namedCurve: "P-256" },
  true,
  ["sign", "verify"],
);
const privateKey = await crypto.subtle.exportKey("jwk", keys.privateKey);

console.log(`VAPID_PUBLIC_KEY=${base64UrlEncode(await crypto.subtle.exportKey("raw", keys.publicKey))}`);
console.log(`VAPID_PRIVATE_KEY=${privateKey.d}`);
console.log("VAPID_SUBJECT=mailto:replace-with-your-email@example.com");
