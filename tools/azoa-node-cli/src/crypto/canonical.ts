import type { KeyObject } from "node:crypto";
import { signMessage } from "./keys.js";

/** §8 H2: all node-admin signed messages share this protocol prefix. */
export const PROTOCOL_PREFIX = "azoa-node-admin/v1";

/**
 * Builds the exact byte sequence that gets signed: prefix + fields joined by
 * "\n", no trailing newline. Every field must be the server-returned string
 * verbatim (never re-serialized locally) — see src/AGENTS.md.
 */
export function buildCanonicalMessage(fields: string[]): string {
  return [PROTOCOL_PREFIX, ...fields].join("\n");
}

export function signCanonical(privateKey: KeyObject, fields: string[]): string {
  return signMessage(privateKey, buildCanonicalMessage(fields));
}

export function enrollMessage(audience: string, enrollmentId: string): string {
  return buildCanonicalMessage(["enroll", audience, enrollmentId]);
}

export function addKeyMessage(audience: string, adminId: string, newFingerprint: string): string {
  return buildCanonicalMessage(["add_key", audience, adminId, newFingerprint]);
}
