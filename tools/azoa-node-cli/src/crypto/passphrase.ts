import {
  createCipheriv,
  createDecipheriv,
  randomBytes,
  scrypt as scryptAsync,
  type ScryptOptions,
} from "node:crypto";
import { promisify } from "node:util";

// `promisify` collapses node:crypto's scrypt overloads to the no-options form;
// the options-taking overload is cast back in explicitly.
const scrypt = promisify(scryptAsync) as (
  password: string,
  salt: Buffer,
  keylen: number,
  options: ScryptOptions,
) => Promise<Buffer>;

/** Binding CLI security amendment (plan.md §8): scrypt N=2^17 r=8 p=1, 256 MiB. */
export const SCRYPT_PARAMS = { N: 2 ** 17, r: 8, p: 1, maxmem: 256 * 1024 * 1024 };
const KEY_LENGTH = 32;
const IV_LENGTH = 12;
const SALT_LENGTH = 16;

export interface EncryptedPayload {
  kdf: "scrypt";
  n: number;
  r: number;
  p: number;
  salt: string;
  iv: string;
  authTag: string;
}

export interface EncryptResult {
  encryption: EncryptedPayload;
  ciphertext: Buffer;
}

async function deriveKey(passphrase: string, salt: Buffer): Promise<Buffer> {
  return (await scrypt(passphrase, salt, KEY_LENGTH, {
    N: SCRYPT_PARAMS.N,
    r: SCRYPT_PARAMS.r,
    p: SCRYPT_PARAMS.p,
    maxmem: SCRYPT_PARAMS.maxmem,
  })) as Buffer;
}

export async function encryptWithPassphrase(
  plaintext: Buffer,
  passphrase: string,
): Promise<EncryptResult> {
  const salt = randomBytes(SALT_LENGTH);
  const iv = randomBytes(IV_LENGTH);
  const key = await deriveKey(passphrase, salt);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const ciphertext = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const authTag = cipher.getAuthTag();
  return {
    encryption: {
      kdf: "scrypt",
      n: SCRYPT_PARAMS.N,
      r: SCRYPT_PARAMS.r,
      p: SCRYPT_PARAMS.p,
      salt: salt.toString("base64"),
      iv: iv.toString("base64"),
      authTag: authTag.toString("base64"),
    },
    ciphertext,
  };
}

/** Throws (AES-GCM auth tag check fails) when the passphrase is wrong. */
export async function decryptWithPassphrase(
  ciphertext: Buffer,
  passphrase: string,
  payload: EncryptedPayload,
): Promise<Buffer> {
  const salt = Buffer.from(payload.salt, "base64");
  const iv = Buffer.from(payload.iv, "base64");
  const authTag = Buffer.from(payload.authTag, "base64");
  const key = (await scrypt(passphrase, salt, KEY_LENGTH, {
    N: payload.n,
    r: payload.r,
    p: payload.p,
    maxmem: SCRYPT_PARAMS.maxmem,
  })) as Buffer;
  const decipher = createDecipheriv("aes-256-gcm", key, iv);
  decipher.setAuthTag(authTag);
  return Buffer.concat([decipher.update(ciphertext), decipher.final()]);
}
