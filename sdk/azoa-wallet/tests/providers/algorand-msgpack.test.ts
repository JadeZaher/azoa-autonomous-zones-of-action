/**
 * Canonical Algorand msgpack encoding — coverage for the encoder, the
 * envelope builder, and `AlgorandProvider.signAndEncodeTransaction()`.
 *
 * Fixtures intentionally use small / known values so the expected byte
 * sequences are computable by hand from the msgpack spec
 * (https://github.com/msgpack/msgpack/blob/master/spec.md) and the
 * Algorand canonical-encoding rules (sorted keys, omit-empty, bin for
 * byte fields, "TX" domain prefix).
 */

import { describe, it, expect } from "vitest";
import { decode as msgpackDecode } from "@msgpack/msgpack";
import { AlgorandProvider } from "../../src/algorand/provider.js";
import {
  encodeAlgorandTransaction,
  encodeCanonicalTxBody,
  buildSignedTransactionEnvelope,
  canonicaliseTxFields,
  decodeAlgorandAddress,
} from "../../src/algorand/msgpack.js";
import { isOk, isErr } from "../../src/core/result.js";
import { AlgorandConfigBuilder, createMockSigner } from "../builders/index.js";

// 58-character valid base32 Algorand-shaped addresses. The first 56 chars
// carry the 32-byte pubkey (we don't care about the trailing checksum bits
// here because decodeAlgorandAddress slices to the first 32 bytes).
// Base32 alphabet (RFC4648): ABCDEFGHIJKLMNOPQRSTUVWXYZ234567 — no 0/1/8/9.
const SENDER_ADDR = "SENDER234567ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMN";
const RECEIVER_ADDR = "RCVR23456ABCDEFGHIJKLMNOPQRSTUVWXYZ234567ABCDEFGHIJKLMNOPQ";

// Pre-encoded inputs reused across cases.
function buildPayDescriptor(overrides: Record<string, unknown> = {}) {
  return {
    type: "pay",
    from: SENDER_ADDR,
    to: RECEIVER_ADDR,
    amount: 1_500_000,
    fee: 1000,
    firstRound: 100,
    lastRound: 1000,
    genesisHash: "R0VORVNJU19IQVNIX0ZJWFRVUkVfMzJfQllURVNfRk9SX1RFU1RT", // 32-byte base64
    genesisId: "testnet-v1.0",
    ...overrides,
  };
}

function txFromObj(obj: unknown) {
  return {
    chain: "algorand",
    format: "json-descriptor" as const,
    bytes: new TextEncoder().encode(JSON.stringify(obj)),
  };
}

describe("Algorand canonical msgpack encoding", () => {
  describe("decodeAlgorandAddress", () => {
    it("returns 32 bytes for a 58-character base32 input", () => {
      const out = decodeAlgorandAddress(SENDER_ADDR);
      expect(out).toBeInstanceOf(Uint8Array);
      expect(out.length).toBe(32);
    });

    it("rejects wrong-length input", () => {
      expect(() => decodeAlgorandAddress("TOOSHORT")).toThrow(/58 base32 chars/);
    });

    it("rejects invalid base32 characters", () => {
      // '0' and '1' are excluded from RFC4648 base32
      const bad = "0".repeat(58);
      expect(() => decodeAlgorandAddress(bad)).toThrow(/Invalid base32/);
    });
  });

  describe("canonicaliseTxFields", () => {
    it("translates long field names to Algorand short names", () => {
      const result = canonicaliseTxFields(buildPayDescriptor());
      expect(result.type).toBe("pay");
      expect(result.snd).toBeInstanceOf(Uint8Array); // from -> snd
      expect(result.rcv).toBeInstanceOf(Uint8Array); // to -> rcv
      expect(result.amt).toBe(1_500_000); // amount -> amt
      expect(result.fee).toBe(1000);
      expect(result.fv).toBe(100); // firstRound -> fv
      expect(result.lv).toBe(1000); // lastRound -> lv
      expect(result.gh).toBeInstanceOf(Uint8Array); // genesisHash -> gh
      expect(result.gen).toBe("testnet-v1.0");
      // Original long names are gone after translation
      expect((result as Record<string, unknown>).from).toBeUndefined();
      expect((result as Record<string, unknown>).to).toBeUndefined();
      expect((result as Record<string, unknown>).amount).toBeUndefined();
    });

    it("omits zero / empty / undefined fields (omit-empty)", () => {
      const result = canonicaliseTxFields(
        buildPayDescriptor({ amount: 0, note: new Uint8Array(0), genesisId: "" })
      );
      expect((result as Record<string, unknown>).amt).toBeUndefined();
      expect((result as Record<string, unknown>).note).toBeUndefined();
      expect((result as Record<string, unknown>).gen).toBeUndefined();
      // Non-empty fields survive
      expect(result.fee).toBe(1000);
    });

    it("decodes byte-typed string fields (addresses + base64) to Uint8Array", () => {
      const result = canonicaliseTxFields(buildPayDescriptor());
      // snd is a decoded address (32 bytes)
      expect((result.snd as Uint8Array).length).toBe(32);
      // gh came from a base64 string and decodes to bytes
      expect(result.gh).toBeInstanceOf(Uint8Array);
    });
  });

  describe("encodeAlgorandTransaction", () => {
    it("prepends 'TX' (0x54 0x58) domain-separation prefix", () => {
      const tx = txFromObj(buildPayDescriptor());
      const out = encodeAlgorandTransaction(tx);
      expect(out[0]).toBe(0x54);
      expect(out[1]).toBe(0x58);
    });

    it("produces sorted-keys msgpack body when decoded", () => {
      const tx = txFromObj(buildPayDescriptor());
      const out = encodeAlgorandTransaction(tx);
      // Strip "TX" prefix → decode → key order is iteration order of the map.
      const body = out.slice(2);
      const decoded = msgpackDecode(body) as Record<string, unknown>;
      const keys = Object.keys(decoded);
      const sorted = [...keys].sort();
      expect(keys).toEqual(sorted);
    });

    it("omits empty fields in the encoded body", () => {
      const txWithZero = txFromObj(
        buildPayDescriptor({ amount: 0, note: new Uint8Array(0) })
      );
      const out = encodeAlgorandTransaction(txWithZero);
      const decoded = msgpackDecode(out.slice(2)) as Record<string, unknown>;
      expect(decoded.amt).toBeUndefined();
      expect(decoded.note).toBeUndefined();
    });

    it("encodes byte fields as msgpack bin (decoded as Uint8Array)", () => {
      const tx = txFromObj(buildPayDescriptor());
      const out = encodeAlgorandTransaction(tx);
      const decoded = msgpackDecode(out.slice(2)) as Record<string, unknown>;
      expect(decoded.snd).toBeInstanceOf(Uint8Array);
      expect(decoded.rcv).toBeInstanceOf(Uint8Array);
      expect(decoded.gh).toBeInstanceOf(Uint8Array);
      expect((decoded.snd as Uint8Array).length).toBe(32);
    });

    it("omit-empty fixture: zero-amt + empty-note tx is strictly shorter than the populated one", () => {
      const populated = encodeAlgorandTransaction(txFromObj(buildPayDescriptor()));
      const minimal = encodeAlgorandTransaction(
        txFromObj(buildPayDescriptor({ amount: 0, note: new Uint8Array(0) }))
      );
      // Dropping `amt` (and never adding `note`) MUST shrink the bytes.
      expect(minimal.length).toBeLessThan(populated.length);
    });

    it("produces stable bytes across encodes (deterministic)", () => {
      const tx = txFromObj(buildPayDescriptor());
      const a = encodeAlgorandTransaction(tx);
      const b = encodeAlgorandTransaction(tx);
      expect(Array.from(a)).toEqual(Array.from(b));
    });
  });

  describe("encodeCanonicalTxBody", () => {
    it("does NOT include the 'TX' prefix (envelope-internal form)", () => {
      const body = encodeCanonicalTxBody(buildPayDescriptor());
      // First byte should be a msgpack map header, not 0x54.
      expect(body[0]).not.toBe(0x54);
      const decoded = msgpackDecode(body) as Record<string, unknown>;
      expect(decoded.type).toBe("pay");
    });
  });

  describe("buildSignedTransactionEnvelope", () => {
    it("produces canonical { sig, txn } map with sorted keys", () => {
      const tx = txFromObj(buildPayDescriptor());
      const sig = new Uint8Array(64).fill(0xab);
      const pubkey = new Uint8Array(32).fill(0xcd);

      const envelope = buildSignedTransactionEnvelope(tx, sig, pubkey);
      const decoded = msgpackDecode(envelope) as { sig: Uint8Array; txn: Record<string, unknown> };

      expect(decoded.sig).toBeInstanceOf(Uint8Array);
      expect((decoded.sig as Uint8Array).length).toBe(64);
      expect(decoded.txn).toBeTypeOf("object");
      expect((decoded.txn as Record<string, unknown>).type).toBe("pay");

      // Top-level key order: 'sig' < 'txn' lexicographically.
      const keys = Object.keys(decoded);
      expect(keys).toEqual(["sig", "txn"]);

      // Inner txn keys are also sorted.
      const innerKeys = Object.keys(decoded.txn);
      expect(innerKeys).toEqual([...innerKeys].sort());
    });

    it("rejects non-64-byte signature inputs", () => {
      const tx = txFromObj(buildPayDescriptor());
      const badSig = new Uint8Array(32); // wrong length
      const pubkey = new Uint8Array(32);
      expect(() => buildSignedTransactionEnvelope(tx, badSig, pubkey)).toThrow(
        /signature must be 64 bytes/
      );
    });
  });
});

describe("AlgorandProvider.signAndEncodeTransaction", () => {
  const provider = new AlgorandProvider(new AlgorandConfigBuilder().onTestnet().build());

  it("returns { signature, encoded } on a json-descriptor payment", async () => {
    const tx = txFromObj(buildPayDescriptor());
    const signer = createMockSigner();

    const result = await provider.signAndEncodeTransaction(tx, signer);

    expect(isOk(result)).toBe(true);
    if (result.ok) {
      expect(result.value.signature).toBeInstanceOf(Uint8Array);
      expect(result.value.signature.length).toBe(64);
      expect(result.value.encoded).toBeInstanceOf(Uint8Array);
      expect(result.value.encoded.length).toBeGreaterThan(64); // sig + txn body
    }
  });

  it("signature is taken over the 'TX'-prefixed canonical bytes (not the raw json descriptor)", async () => {
    const tx = txFromObj(buildPayDescriptor());
    const signer = createMockSigner();

    const result = await provider.signAndEncodeTransaction(tx, signer);
    expect(isOk(result)).toBe(true);
    if (!result.ok) return;

    // Mock signer XORs the first N bytes of the message with 0xff. So the
    // first two bytes of the signature should match `'T' ^ 0xff` and
    // `'X' ^ 0xff` — confirming the signer received the "TX"-prefixed
    // bytes, not the original JSON descriptor.
    expect(result.value.signature[0]).toBe(0x54 ^ 0xff);
    expect(result.value.signature[1]).toBe(0x58 ^ 0xff);
  });

  it("encoded envelope round-trips through msgpack as { sig, txn }", async () => {
    const tx = txFromObj(buildPayDescriptor());
    const signer = createMockSigner();
    const result = await provider.signAndEncodeTransaction(tx, signer);
    expect(isOk(result)).toBe(true);
    if (!result.ok) return;

    const decoded = msgpackDecode(result.value.encoded) as {
      sig: Uint8Array;
      txn: Record<string, unknown>;
    };
    expect(decoded.sig).toBeInstanceOf(Uint8Array);
    expect(decoded.txn.type).toBe("pay");
    expect(decoded.txn.amt).toBe(1_500_000);
    expect(decoded.txn.snd).toBeInstanceOf(Uint8Array);
  });

  it("rejects non-json-descriptor formats with UNSUPPORTED_OPERATION", async () => {
    const tx = {
      chain: "algorand",
      format: "native" as const,
      bytes: new Uint8Array([1, 2, 3]),
    };
    const signer = createMockSigner();

    const result = await provider.signAndEncodeTransaction(tx, signer);
    expect(isErr(result)).toBe(true);
    if (!result.ok) {
      expect(result.error.message).toContain("json-descriptor");
    }
  });

  it("surfaces signer errors as SIGNING_ERROR Result", async () => {
    const tx = txFromObj(buildPayDescriptor());
    const failingSigner = {
      publicKey: new Uint8Array(32),
      sign: () => Promise.reject(new Error("hsm offline")),
    };
    const result = await provider.signAndEncodeTransaction(tx, failingSigner);
    expect(isErr(result)).toBe(true);
    if (!result.ok) {
      expect(result.error.message).toContain("hsm offline");
    }
  });
});
