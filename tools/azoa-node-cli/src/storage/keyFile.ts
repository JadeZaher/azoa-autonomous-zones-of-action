import type { KeyObject } from "node:crypto";
import { AzoaCliError } from "../errors.js";
import { keyPath, keysDir, isUnderOneDrive } from "./paths.js";
import { readJsonFile, writeJsonFileSecure } from "./fsutil.js";
import { mkdirSync } from "node:fs";
import {
  exportPkcs8Der,
  importPkcs8Der,
  keyPairFromPrivateKey,
  type Ed25519KeyPair,
} from "../crypto/keys.js";
import {
  decryptWithPassphrase,
  encryptWithPassphrase,
  type EncryptedPayload,
} from "../crypto/passphrase.js";
import type { ProfileKind } from "./config.js";

interface KeyFile {
  version: 1;
  kind: ProfileKind;
  publicKey: string;
  fingerprint: string;
  encryption: EncryptedPayload | null;
  privateKey: string;
}

export interface KeyFileWarning {
  oneDrive: boolean;
}

export function checkKeyFileLocation(name: string): KeyFileWarning {
  return { oneDrive: isUnderOneDrive(keyPath(name)) };
}

export async function writeKeyFile(
  name: string,
  keyPair: Ed25519KeyPair,
  kind: ProfileKind,
  passphrase: string | null,
): Promise<void> {
  mkdirSync(keysDir(), { recursive: true });
  const der = exportPkcs8Der(keyPair.privateKey);
  let encryption: EncryptedPayload | null = null;
  let privateKeyField: string;
  if (passphrase !== null) {
    const encrypted = await encryptWithPassphrase(der, passphrase);
    encryption = encrypted.encryption;
    privateKeyField = encrypted.ciphertext.toString("base64");
  } else {
    privateKeyField = der.toString("base64");
  }
  const file: KeyFile = {
    version: 1,
    kind,
    publicKey: keyPair.publicKeyB64Url,
    fingerprint: keyPair.fingerprint,
    encryption,
    privateKey: privateKeyField,
  };
  writeJsonFileSecure(keyPath(name), file);
}

export function keyFileIsEncrypted(name: string): boolean {
  return readKeyFileMeta(name).encrypted;
}

export interface KeyFileMeta {
  publicKey: string;
  fingerprint: string;
  kind: ProfileKind;
  encrypted: boolean;
}

/** Reads only the public metadata (no decryption, no private key exposure). */
export function readKeyFileMeta(name: string): KeyFileMeta {
  const file = readJsonFile<KeyFile>(keyPath(name));
  if (!file) throw AzoaCliError.usage(`Key file not found: ${keyPath(name)}`);
  return {
    publicKey: file.publicKey,
    fingerprint: file.fingerprint,
    kind: file.kind,
    encrypted: file.encryption !== null,
  };
}

export async function readKeyFile(
  name: string,
  passphrase: string | null,
): Promise<Ed25519KeyPair> {
  const file = readJsonFile<KeyFile>(keyPath(name));
  if (!file) throw AzoaCliError.usage(`Key file not found: ${keyPath(name)}`);
  let der: Buffer;
  if (file.encryption) {
    if (passphrase === null) {
      throw AzoaCliError.usage("This key is passphrase-protected; a passphrase is required.");
    }
    der = await decryptWithPassphrase(
      Buffer.from(file.privateKey, "base64"),
      passphrase,
      file.encryption,
    );
  } else {
    der = Buffer.from(file.privateKey, "base64");
  }
  const privateKey: KeyObject = importPkcs8Der(der);
  return keyPairFromPrivateKey(privateKey);
}
