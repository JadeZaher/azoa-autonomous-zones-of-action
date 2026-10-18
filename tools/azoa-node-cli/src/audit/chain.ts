import { createHash } from "node:crypto";

export const GENESIS_PREV_HASH = "0".repeat(64);

export interface AuditRow {
  seq: number;
  at: string;
  actor: string;
  action: string;
  target: string;
  outcome: string;
  detail: string;
  prevHash: string;
  hash: string;
  // metadata fields below are not hashed (plan.md §8 H4/audit-hash)
  amr?: string[];
  ip?: string;
  requestId?: string;
}

/**
 * plan.md §8 audit-hash formula, verbatim:
 * hash = hex(sha256(utf8(prevHash + "\n" + seq + "\n" + at + "\n" + actor + "\n" +
 *                         action + "\n" + target + "\n" + outcome + "\n" + detail)))
 */
export function computeRowHash(row: Pick<AuditRow, "prevHash" | "seq" | "at" | "actor" | "action" | "target" | "outcome" | "detail">): string {
  const material = [
    row.prevHash,
    String(row.seq),
    row.at,
    row.actor,
    row.action,
    row.target,
    row.outcome,
    row.detail,
  ].join("\n");
  return createHash("sha256").update(Buffer.from(material, "utf8")).digest("hex");
}

export interface VerifyChainResult {
  ok: boolean;
  verifiedThroughSeq: number;
  headHash: string;
  firstBrokenSeq: number | null;
}

export interface PinnedState {
  seq: number;
  hash: string;
}

/**
 * Recomputes the chain over `rows` (expected contiguous, ascending by seq,
 * starting right after a genesis or a prior pin). Detects: (1) a gap/reorder
 * in `seq` (M2 — a deleted-then-relinked row can still hash-chain correctly
 * if the attacker also recomputes everything downstream, but can't fake a
 * contiguous seq without inserting a real row), (2) any row whose stored
 * hash does not match the recomputed hash (tampered detail/fields),
 * (3) a head that regressed behind a previously pinned (seq, hash).
 */
export function verifyChain(rows: AuditRow[], pin: PinnedState | null): VerifyChainResult {
  let prevHash = pin?.hash ?? GENESIS_PREV_HASH;
  let verifiedThroughSeq = pin?.seq ?? 0;
  let expectedSeq = (pin?.seq ?? 0) + 1;
  let firstBrokenSeq: number | null = null;

  for (const row of rows) {
    if (row.seq !== expectedSeq) {
      firstBrokenSeq = row.seq;
      break;
    }
    if (row.prevHash !== prevHash) {
      firstBrokenSeq = row.seq;
      break;
    }
    const recomputed = computeRowHash({ ...row, prevHash });
    if (recomputed !== row.hash) {
      firstBrokenSeq = row.seq;
      break;
    }
    prevHash = row.hash;
    verifiedThroughSeq = row.seq;
    expectedSeq = row.seq + 1;
  }

  return {
    ok: firstBrokenSeq === null,
    verifiedThroughSeq,
    headHash: prevHash,
    firstBrokenSeq,
  };
}

/** True when the server-reported head regressed behind what we previously pinned. */
export function headRegressed(pin: PinnedState | null, serverHead: PinnedState): boolean {
  if (!pin) return false;
  return serverHead.seq < pin.seq;
}
