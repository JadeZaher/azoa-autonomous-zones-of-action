import { describe, expect, it } from "vitest";
import { fingerprintOf, privateKeyFromSeed, keyPairFromPrivateKey, signMessage } from "../src/crypto/keys.js";
import { buildCanonicalMessage } from "../src/crypto/canonical.js";

/**
 * plan.md §8 H2 golden vectors (RFC 8032 test-1 seed). Both lanes must
 * reproduce these exact signatures — this is the contract tripwire: if the
 * canonical message format or signing primitive ever drifts from the
 * backend, this test fails first.
 */
const SEED_HEX = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
const EXPECTED_PUBLIC_KEY = "11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo";
const EXPECTED_FINGERPRINT = "21fe31dfa154a261";

describe("golden vectors (plan.md §8 H2)", () => {
  const keyPair = keyPairFromPrivateKey(privateKeyFromSeed(Buffer.from(SEED_HEX, "hex")));

  it("derives the expected public key and fingerprint from the RFC 8032 seed", () => {
    expect(keyPair.publicKeyB64Url).toBe(EXPECTED_PUBLIC_KEY);
    expect(fingerprintOf(keyPair.publicKeyRaw)).toBe(EXPECTED_FINGERPRINT);
    expect(keyPair.fingerprint).toBe(EXPECTED_FINGERPRINT);
  });

  it("reproduces the exact root_login signature", () => {
    const message = buildCanonicalMessage([
      "root_login",
      "https://node.example.test",
      "0b9e2f5a-6f0c-4e57-9a51-2b1f0e0c3d41",
      "q83vEjRWeJCrze8SNFZ4kKvN7xI0VniQq83vEjRWeJA",
      "2026-10-18T17:00:00.0000000+00:00",
    ]);
    expect(signMessage(keyPair.privateKey, message)).toBe(
      "-NPZcjbxjAdPdW_KYSK6scxyFybe8EXi6VWz2clrUYPj43I-7dJJiYQTP8HFfpgixA1ChD2ymBji1Kl9ylA-Bw",
    );
  });

  it("reproduces the exact admin_login signature", () => {
    const message = buildCanonicalMessage([
      "admin_login",
      "https://node.example.test",
      "7d4c1b2a-0e9f-4a8b-8c7d-6e5f4a3b2c1d",
      "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
      "2026-10-18T17:00:00.0000000+00:00",
    ]);
    expect(signMessage(keyPair.privateKey, message)).toBe(
      "daj9AS74SQyY-krCU38k00lgAcQXJMMtfx8u5DaXnRzGupenpanbZXfLiYPBkApyK9yAo9IjF-6OXNXzH0mFAQ",
    );
  });

  it("reproduces the exact enroll signature", () => {
    const message = buildCanonicalMessage(["enroll", "https://node.example.test", "3f2e1d0c-9b8a-4776-8554-433221100fed"]);
    expect(signMessage(keyPair.privateKey, message)).toBe(
      "8iizKreiGkkhhsPN7BpwI2IOHPQercUI2Je_Xg6BGTV4uWbMnMpQ_LaYcIYKmSOSa_VGbQgHsdembgsGQkSHAA",
    );
  });
});
