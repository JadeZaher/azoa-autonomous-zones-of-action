/**
 * Canonical Algorand transaction encoding.
 *
 * Algorand's signing payload is NOT plain JSON bytes — it is a canonical
 * msgpack-encoded object with these rules:
 *
 *   1. Map keys MUST be Algorand's 2-3 character short field names
 *      (`snd`, `rcv`, `amt`, `fv`, etc.) — never the long human-readable
 *      names the SDK uses in `json-descriptor` payloads (`sender`,
 *      `receiver`, `amount`, `firstValid`, ...).
 *   2. Map keys MUST be encoded in sorted (lexicographic, byte-wise) order.
 *      `@msgpack/msgpack`'s `{ sortKeys: true }` option handles this.
 *   3. Zero / empty / unset fields MUST be omitted from the encoded object.
 *      Algorand's reference implementation tags every field with
 *      `codec:"omitempty"`; without omit-empty the produced bytes are
 *      canonical msgpack but NOT canonical *Algorand* encoding, and the
 *      `algod` node will refuse the transaction.
 *      Concretely: omit `amt: 0`, `fee: 0`, `note: <empty bytes>`, any
 *      `undefined`/`null` field, and any empty list.
 *   4. Byte fields (addresses, hashes, notes, group, lease, programs) MUST
 *      be encoded as msgpack `bin` (Uint8Array), never as base64 strings.
 *      Algorand addresses are 58-character base32 strings; the canonical
 *      encoding stores them as the underlying 32-byte public key (the
 *      checksum trailer is dropped — it is verified, not signed over).
 *   5. The bytes-to-sign are the canonical msgpack output prefixed by
 *      ASCII "TX" (`0x54 0x58`). This domain-separation prefix prevents
 *      a signature over a transaction from being replayed as a signature
 *      over arbitrary data on the same key.
 *
 * The submittable signed-transaction envelope is a second canonical
 * msgpack object: `{ sig: <ed25519 signature>, txn: <tx fields object> }`
 * (also sorted-keys, omit-empty). The "TX" prefix is NOT part of the
 * submitted envelope — it only sits in front of the bytes that were
 * signed.
 *
 * References:
 *  - go-algorand's `protocol/codec.go` (sorted-keys + omit-empty).
 *  - The transaction struct tags in `data/transactions/transaction.go`.
 *  - `crypto/util.go` `SignedHashHashable` (the "TX" prefix lives in
 *    `protocol.HashID = "TX"`).
 */

import { encode as msgpackEncode } from "@msgpack/msgpack";

// ─── Field name mapping ──────────────────────────────────────────────────────
// The SDK's json-descriptor format uses long, human-readable field names.
// Algorand's canonical encoding uses 2-3 character abbreviations. The two
// must be kept in lock-step: every long name that the provider's
// `buildPaymentTx`, `buildAsaTransferTx`, `buildMint`, `buildBurn` (and any
// future builders) might emit needs a row here.

/** Long → short field name map for transaction fields. */
const SHORT: Readonly<Record<string, string>> = Object.freeze({
  // Common header fields
  type: "type",
  from: "snd", // sender (32 raw bytes)
  sender: "snd",
  fee: "fee",
  firstRound: "fv", // first valid
  firstValid: "fv",
  lastRound: "lv", // last valid
  lastValid: "lv",
  genesisHash: "gh", // 32 raw bytes
  genesisId: "gen",
  note: "note", // raw bytes
  group: "grp", // 32 raw bytes
  lease: "lx", // 32 raw bytes
  rekeyTo: "rekey", // 32 raw bytes

  // pay
  to: "rcv", // receiver (32 raw bytes)
  receiver: "rcv",
  amount: "amt",
  closeRemainderTo: "close",

  // axfer
  assetIndex: "xaid",
  assetSender: "asnd",
  assetReceiver: "arcv",
  assetAmount: "aamt",
  assetCloseTo: "aclose",

  // acfg (asset create / reconfigure / destroy)
  assetTotal: "t",
  assetDecimals: "dc",
  assetDefaultFrozen: "df",
  assetUnitName: "un",
  assetName: "an",
  assetURL: "au",
  assetMetadataHash: "am",
  assetManager: "m",
  assetReserve: "r",
  assetFreeze: "f",
  assetClawback: "c",

  // appl
  appIndex: "apid",
  onCompletion: "apan",
  appArgs: "apaa",
  foreignAssets: "apas",
  foreignAccounts: "apat",
  foreignApps: "apfa",
  approvalProgram: "apap",
  clearProgram: "apsu",
  globalSchema: "apgs",
  localSchema: "apls",
});

/** Which short fields are byte (`bin`) typed, not string/int. */
const BIN_FIELDS: ReadonlySet<string> = new Set([
  "snd",
  "rcv",
  "close",
  "gh",
  "note",
  "grp",
  "lx",
  "rekey",
  "asnd",
  "arcv",
  "aclose",
  "m",
  "r",
  "f",
  "c",
  "am",
  "apap",
  "apsu",
]);

// ─── Address codec (base32 → 32-byte public key) ─────────────────────────────
// Algorand addresses: 58 base32 chars = 32-byte pubkey + 4-byte checksum.
// We slice off the last 4 bytes; algod verifies the checksum on the wire from
// the address bytes themselves (a spend tx is signed over the pubkey, not
// over the checksum).

const B32_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
const B32_LOOKUP: Uint8Array = (() => {
  const t = new Uint8Array(256).fill(255);
  for (let i = 0; i < B32_ALPHABET.length; i++) t[B32_ALPHABET.charCodeAt(i)] = i;
  return t;
})();

/**
 * Decode an Algorand 58-character base32 address into its 32-byte raw
 * public key (the canonical wire-format representation of `snd`/`rcv`/etc.).
 *
 * Throws if the input is not a 58-character RFC4648 base32 string.
 */
export function decodeAlgorandAddress(address: string): Uint8Array {
  if (address.length !== 58) {
    throw new Error(
      `Invalid Algorand address: expected 58 base32 chars, got ${address.length}`
    );
  }
  // 58 base32 chars * 5 bits = 290 bits = 36.25 bytes → 36 bytes
  // (32 pubkey + 4 checksum).
  const out = new Uint8Array(36);
  let acc = 0;
  let bits = 0;
  let outIdx = 0;
  for (let i = 0; i < address.length; i++) {
    const code = address.charCodeAt(i);
    const v = B32_LOOKUP[code];
    if (v === undefined || v === 255) {
      throw new Error(`Invalid base32 character at position ${i}: '${address[i]}'`);
    }
    acc = (acc << 5) | v;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      if (outIdx < out.length) {
        out[outIdx++] = (acc >> bits) & 0xff;
      }
    }
  }
  return out.slice(0, 32);
}

// ─── omit-empty + name-shortening canonicalisation ───────────────────────────

/** True when a value should be omit-empty'd from the canonical encoding. */
function isEmpty(value: unknown): boolean {
  if (value === undefined || value === null) return true;
  if (typeof value === "number") return value === 0;
  if (typeof value === "bigint") return value === 0n;
  if (typeof value === "string") return value.length === 0;
  if (value instanceof Uint8Array) return value.length === 0;
  if (Array.isArray(value)) return value.length === 0;
  // Plain `{}` — including the result of `JSON.parse(JSON.stringify(new
  // Uint8Array(0)))` which yields `{}` — counts as empty.
  if (typeof value === "object" && Object.keys(value as object).length === 0) return true;
  return false;
}

/**
 * `JSON.stringify(new Uint8Array([1, 2, 3]))` produces `{"0":1,"1":2,"2":3}`.
 * After `JSON.parse` we get a plain object whose keys are stringified
 * non-negative integers and whose values are byte-range numbers. Detect that
 * shape so we can losslessly recover the original bytes from JSON-serialized
 * descriptors emitted by the provider's builders.
 */
function plainObjectIsByteArray(value: object): boolean {
  const keys = Object.keys(value);
  if (keys.length === 0) return false;
  for (let i = 0; i < keys.length; i++) {
    if (keys[i] !== String(i)) return false;
    const v = (value as Record<string, unknown>)[keys[i]!];
    if (typeof v !== "number" || v < 0 || v > 255 || !Number.isInteger(v)) return false;
  }
  return true;
}

function byteArrayObjectToBytes(value: object): Uint8Array {
  const len = Object.keys(value).length;
  const out = new Uint8Array(len);
  for (let i = 0; i < len; i++) {
    out[i] = (value as Record<string, number>)[String(i)]!;
  }
  return out;
}

/**
 * Convert a value into its canonical msgpack-encoder representation.
 *
 * The two fixups this needs to handle:
 *  - String values for byte fields are interpreted as either 58-char
 *    Algorand addresses (decoded → 32 bytes) or base64-encoded raw bytes
 *    (which JSON descriptors use when a binary field came from the wire).
 *  - Already-decoded `Uint8Array` values pass through unchanged.
 */
function coerceField(shortName: string, value: unknown): unknown {
  if (BIN_FIELDS.has(shortName)) {
    if (value instanceof Uint8Array) return value;
    if (typeof value === "string") {
      // Heuristic: a 58-char string is an Algorand address; everything else
      // is base64-encoded bytes (JSON descriptors can't carry binary
      // natively, so emitters round-trip byte fields through base64).
      if (value.length === 58) return decodeAlgorandAddress(value);
      return base64ToBytes(value);
    }
    // `JSON.stringify(new Uint8Array([...]))` produces a numeric-keyed plain
    // object; recover the original bytes losslessly so descriptors built by
    // the SDK builders survive a JSON round-trip.
    if (value !== null && typeof value === "object" && plainObjectIsByteArray(value as object)) {
      return byteArrayObjectToBytes(value as object);
    }
    throw new Error(
      `Field '${shortName}' must be Uint8Array, base64 string, or 58-char Algorand address — got ${typeof value}`
    );
  }
  // Non-binary fields (numbers, strings, plain objects) are returned as-is.
  // NOTE: omit-empty is top-level only. Nested map fields like `apgs`/`apls`
  // (Algorand state schemas) will not have their inner zero/empty values
  // stripped. Currently unreachable from supported tx builders (pay, asa-transfer,
  // mint, burn are flat). Recurse here when adding app-create / schema-update support.
  return value;
}

/** Minimal pure-JS base64 decoder — no Buffer / atob. */
function base64ToBytes(b64: string): Uint8Array {
  const CHARS = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  const lookup = new Uint8Array(256).fill(255);
  for (let i = 0; i < CHARS.length; i++) lookup[CHARS.charCodeAt(i)] = i;
  const s = b64.replace(/[\s=]+/g, "");
  const out = new Uint8Array(Math.floor((s.length * 3) / 4));
  let idx = 0;
  for (let i = 0; i < s.length; i += 4) {
    const a = lookup[s.charCodeAt(i)]!;
    const b = lookup[s.charCodeAt(i + 1)]!;
    const c = i + 2 < s.length ? lookup[s.charCodeAt(i + 2)]! : 0;
    const d = i + 3 < s.length ? lookup[s.charCodeAt(i + 3)]! : 0;
    out[idx++] = (a << 2) | (b >> 4);
    if (i + 2 < s.length) out[idx++] = ((b & 0xf) << 4) | (c >> 2);
    if (i + 3 < s.length) out[idx++] = ((c & 0x3) << 6) | d;
  }
  return out.slice(0, idx);
}

/**
 * Take a json-descriptor object (long-named, JSON-friendly) and produce the
 * canonical-encoded object: short-named keys, omit-empty applied, byte
 * fields coerced to Uint8Array.
 *
 * Map keys are NOT yet sorted here — `@msgpack/msgpack` handles that at
 * encode time when `sortKeys: true`. We only normalise *contents*.
 *
 * Exported so tests can introspect the intermediate shape without going
 * through a msgpack round-trip.
 */
export function canonicaliseTxFields(descriptor: Record<string, unknown>): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, rawValue] of Object.entries(descriptor)) {
    // If the descriptor already used short names, prefer them as-is;
    // otherwise translate the long name. Unknown keys are kept verbatim —
    // they will round-trip through msgpack but algod will reject them, so
    // this is a fail-fast surface for typos rather than a silent drop.
    const short = SHORT[key] ?? key;
    if (isEmpty(rawValue)) continue;
    const value = coerceField(short, rawValue);
    if (isEmpty(value)) continue; // empty-after-coercion (e.g. empty bytes)
    out[short] = value;
  }
  return out;
}

// ─── Public API ──────────────────────────────────────────────────────────────

/** ASCII "TX" — Algorand's signing-domain separator. */
const TX_PREFIX = new Uint8Array([0x54, 0x58]);

/**
 * Produce the canonical Algorand bytes-to-sign for a transaction.
 *
 * Steps:
 *  1. Decode the JSON descriptor that the SDK's builders emit.
 *  2. Translate long field names → Algorand short names.
 *  3. Apply omit-empty (zero / null / empty bytes / empty array → drop).
 *  4. Coerce byte fields (addresses, hashes) to Uint8Array.
 *  5. Encode with `@msgpack/msgpack` + `sortKeys: true`.
 *  6. Prepend the "TX" domain-separation prefix.
 *
 * The returned bytes are what `ed25519.sign(message, privateKey)` operates
 * on. The encoded transaction body (without the "TX" prefix) is what goes
 * into the `txn` slot of the signed-transaction envelope.
 */
export function encodeAlgorandTransaction(tx: { bytes: Uint8Array }): Uint8Array {
  const descriptor = JSON.parse(new TextDecoder().decode(tx.bytes)) as Record<string, unknown>;
  const canonical = canonicaliseTxFields(descriptor);
  const encoded = msgpackEncode(canonical, { sortKeys: true });
  const out = new Uint8Array(TX_PREFIX.length + encoded.length);
  out.set(TX_PREFIX, 0);
  out.set(encoded, TX_PREFIX.length);
  return out;
}

/**
 * Encode JUST the canonical transaction body (without the "TX" prefix).
 *
 * This is the form used inside the signed-transaction envelope's `txn`
 * field. It is exposed so tests can assert sorted-key order on the body
 * directly, and so the envelope helper does not have to strip a prefix
 * back off.
 */
export function encodeCanonicalTxBody(descriptor: Record<string, unknown>): Uint8Array {
  return msgpackEncode(canonicaliseTxFields(descriptor), { sortKeys: true });
}

/**
 * Build the submittable signed-transaction envelope:
 *   `{ sig: <64-byte ed25519 signature>, txn: <canonical tx fields object> }`
 *
 * Encoded with `@msgpack/msgpack` + `sortKeys: true`. The result is exactly
 * the body of `POST /v2/transactions` on algod (the node accepts the bare
 * envelope; no extra wrapping).
 *
 * Note: the `txn` slot here holds the canonical *object*, not its already-
 * encoded bytes. `@msgpack/msgpack` re-serialises the whole envelope in a
 * single pass, which is what go-algorand does in its `SignedTxn` codec.
 *
 * The `_signerPublicKey` argument is currently unused but reserved for the
 * `sgnr` field that Algorand requires when the signer differs from the
 * transaction's `snd` (rekeyed accounts). When that path is needed, a
 * follow-up can drop the underscore and add `sgnr: signerPublicKey` to the
 * envelope under an `if (signerPublicKey && !pubkeyEquals(signerPublicKey, snd))` guard.
 */
export function buildSignedTransactionEnvelope(
  tx: { bytes: Uint8Array },
  signature: Uint8Array,
  _signerPublicKey: Uint8Array
): Uint8Array {
  if (signature.length !== 64) {
    throw new Error(`ed25519 signature must be 64 bytes, got ${signature.length}`);
  }
  const descriptor = JSON.parse(new TextDecoder().decode(tx.bytes)) as Record<string, unknown>;
  const txnFields = canonicaliseTxFields(descriptor);
  const envelope = { sig: signature, txn: txnFields };
  return msgpackEncode(envelope, { sortKeys: true });
}
