import { describe, expect, it } from "vitest";
import { computeRowHash, GENESIS_PREV_HASH, headRegressed, verifyChain, type AuditRow } from "../src/audit/chain.js";

function buildRow(partial: Omit<AuditRow, "hash">): AuditRow {
  return { ...partial, hash: computeRowHash(partial) };
}

function buildFixtureChain(): AuditRow[] {
  const row1 = buildRow({ seq: 1, at: "2026-10-18T17:00:00.0000000+00:00", actor: "root:21fe31dfa154a261", action: "auth.session", target: "root:21fe31dfa154a261", outcome: "ok", detail: "", prevHash: GENESIS_PREV_HASH });
  const row2 = buildRow({ seq: 2, at: "2026-10-18T17:01:00.0000000+00:00", actor: "root:21fe31dfa154a261", action: "admins.create", target: "admin-1", outcome: "ok", detail: '{"username":"alice"}', prevHash: row1.hash });
  const row3 = buildRow({ seq: 3, at: "2026-10-18T17:02:00.0000000+00:00", actor: "admin:admin-1", action: "auth.session", target: "admin:admin-1", outcome: "ok", detail: "", prevHash: row2.hash });
  return [row1, row2, row3];
}

describe("audit chain recompute (plan.md §8 H4/H5 formula)", () => {
  it("verifies a genuinely valid chain end to end", () => {
    const rows = buildFixtureChain();
    const result = verifyChain(rows, null);
    expect(result.ok).toBe(true);
    expect(result.verifiedThroughSeq).toBe(3);
    expect(result.headHash).toBe(rows[2]!.hash);
    expect(result.firstBrokenSeq).toBeNull();
  });

  it("detects a tampered detail field even though the stored hash field itself is untouched", () => {
    const rows = buildFixtureChain();
    // Mutate detail only — hash still says what it said before tampering, so detail/hash now disagree.
    rows[1] = { ...rows[1]!, detail: '{"username":"mallory"}' };
    const result = verifyChain(rows, null);
    expect(result.ok).toBe(false);
    expect(result.firstBrokenSeq).toBe(2);
    expect(result.verifiedThroughSeq).toBe(1);
  });

  it("detects a broken prevHash link (row 3 no longer points at row 2's real hash)", () => {
    const rows = buildFixtureChain();
    rows[2] = { ...rows[2]!, prevHash: "f".repeat(64) };
    const result = verifyChain(rows, null);
    expect(result.ok).toBe(false);
    expect(result.firstBrokenSeq).toBe(3);
  });

  it("flags a head regression against a previously pinned (seq, hash)", () => {
    const rows = buildFixtureChain();
    const pin = { seq: rows[2]!.seq, hash: rows[2]!.hash };
    // Server now reports an earlier seq than what we already pinned — e.g. a restore from backup.
    const serverHead = { seq: 2, hash: rows[1]!.hash };
    expect(headRegressed(pin, serverHead)).toBe(true);
    expect(headRegressed(null, serverHead)).toBe(false);
    expect(headRegressed(pin, { seq: 4, hash: "irrelevant" })).toBe(false);
  });

  it("resumes verification from a pin instead of re-walking the genesis", () => {
    const rows = buildFixtureChain();
    const pin = { seq: rows[0]!.seq, hash: rows[0]!.hash };
    const result = verifyChain(rows.slice(1), pin);
    expect(result.ok).toBe(true);
    expect(result.verifiedThroughSeq).toBe(3);
  });
});
