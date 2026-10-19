import {
  createPrivateKey,
  createPublicKey,
  generateKeyPairSync,
  sign as nodeSign,
  verify as nodeVerify,
  type KeyObject,
} from "node:crypto";
import { createHash } from "node:crypto";
import { fromBase64Url, toBase64Url } from "./encoding.js";

/**
 * RFC 8410 PKCS8 DER wrapper for a raw 32-byte Ed25519 seed:
 * `30 2e 02 01 00 30 05 06 03 2b 65 70 04 22 04 20 <seed>`. Fixed 16-byte
 * prefix, see src/AGENTS.md "Canonical signing".
 */
const PKCS8_ED25519_PREFIX = Buffer.from("302e020100300506032b657004220420", "hex");

export interface Ed25519KeyPair {
  privateKey: KeyObject;
  publicKey: KeyObject;
  publicKeyRaw: Buffer;
  publicKeyB64Url: string;
  fingerprint: string;
}

/** sha256(raw 32-byte public key) -> first 16 hex chars. */
export function fingerprintOf(publicKeyRaw: Buffer): string {
  return createHash("sha256").update(publicKeyRaw).digest("hex").slice(0, 16);
}

function rawPublicKey(publicKey: KeyObject): Buffer {
  return publicKey.export({ type: "spki", format: "der" }).subarray(-32);
}

export function keyPairFromPrivateKey(privateKey: KeyObject): Ed25519KeyPair {
  const publicKey = createPublicKey(privateKey);
  const publicKeyRaw = rawPublicKey(publicKey);
  return {
    privateKey,
    publicKey,
    publicKeyRaw,
    publicKeyB64Url: toBase64Url(publicKeyRaw),
    fingerprint: fingerprintOf(publicKeyRaw),
  };
}

/** Rebuild a KeyObject from a raw 32-byte Ed25519 seed (used by golden-vector tests). */
export function privateKeyFromSeed(seed: Buffer): KeyObject {
  if (seed.length !== 32) {
    throw new Error(`Ed25519 seed must be 32 bytes, got ${seed.length}`);
  }
  const der = Buffer.concat([PKCS8_ED25519_PREFIX, seed]);
  return createPrivateKey({ key: der, format: "der", type: "pkcs8" });
}

export function generateEd25519KeyPair(): Ed25519KeyPair {
  const { privateKey } = generateKeyPairSync("ed25519");
  return keyPairFromPrivateKey(privateKey);
}

/** Export the PKCS8 DER bytes for on-disk storage (see storage/keyFile.ts). */
export function exportPkcs8Der(privateKey: KeyObject): Buffer {
  return privateKey.export({ type: "pkcs8", format: "der" }) as Buffer;
}

export function importPkcs8Der(der: Buffer): KeyObject {
  return createPrivateKey({ key: der, format: "der", type: "pkcs8" });
}

export function importPublicKeyFromB64Url(b64url: string): KeyObject {
  const raw = fromBase64Url(b64url);
  if (raw.length !== 32) {
    throw new Error(`Ed25519 public key must be 32 bytes, got ${raw.length}`);
  }
  const der = Buffer.concat([
    Buffer.from("302a300506032b6570032100", "hex"),
    raw,
  ]);
  return createPublicKey({ key: der, format: "der", type: "spki" });
}

/** Sign UTF-8 `message` bytes with Ed25519; returns base64url signature. */
export function signMessage(privateKey: KeyObject, message: string): string {
  const sig = nodeSign(null, Buffer.from(message, "utf8"), privateKey);
  return toBase64Url(sig);
}

export function verifySignature(
  publicKeyB64Url: string,
  message: string,
  signatureB64Url: string,
): boolean {
  const publicKey = importPublicKeyFromB64Url(publicKeyB64Url);
  const sig = fromBase64Url(signatureB64Url);
  return nodeVerify(null, Buffer.from(message, "utf8"), publicKey, sig);
}
