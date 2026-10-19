import { AzoaCliError } from "../errors.js";
import { headRegressed, verifyChain, type AuditRow, type PinnedState } from "../audit/chain.js";
import { httpClientFor, requireSession, resolveProfile } from "../context.js";
import { emit } from "../io/output.js";
import { loadAuditPin, saveAuditPin } from "../storage/auditPin.js";

export interface AuditTailOptions {
  profile?: string;
  after?: number;
  limit?: number;
  follow?: boolean;
  json: boolean;
}

interface AuditPage {
  rows: AuditRow[];
  head: PinnedState;
}

export async function runAuditTail(options: AuditTailOptions): Promise<AuditRow[]> {
  if (options.follow) {
    // plan.md §8 CLI binding: "audit tail --follow cut."
    throw AzoaCliError.usage("'audit tail --follow' is not implemented (cut per plan.md §8).");
  }
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const limit = Math.min(options.limit ?? 50, 500);
  const after = options.after ?? 0;
  const { data } = await httpClientFor(resolved).call<AuditPage>(
    "GET",
    `/api/node-admin/audit?afterSeq=${after}&limit=${limit}`,
  );
  emit(
    options.json,
    data.rows.map((r) => `#${r.seq} ${r.at} ${r.actor} ${r.action} -> ${r.target} [${r.outcome}]`).join("\n") || "(no rows)",
    data.rows,
  );
  return data.rows;
}

interface VerifyEndpointResponse {
  ok: boolean;
  fromSeq: number;
  verifiedThroughSeq: number;
  headHash: string;
  firstBrokenSeq?: number;
}

export interface AuditVerifyOptions {
  profile?: string;
  /** Ignore any local pin and verify the whole chain from genesis (lane A1: can answer 429 RATE_LIMITED). */
  full?: boolean;
  /** Explicit override for the server-side verify window's start; defaults to the local pin. */
  fromSeq?: number;
  json: boolean;
}

const MAX_AUDIT_PAGE_SIZE = 500;

/** M2: fetches rows after `afterSeq`, refusing to loop forever on a server that never advances. */
async function fetchRowsSince(
  client: { call: <T>(method: string, path: string) => Promise<{ data: T }> },
  afterSeq: number,
  throughSeq: number,
): Promise<AuditRow[]> {
  const rows: AuditRow[] = [];
  let cursor = afterSeq;
  while (cursor < throughSeq) {
    const { data } = await client.call<AuditPage>("GET", `/api/node-admin/audit?afterSeq=${cursor}&limit=${MAX_AUDIT_PAGE_SIZE}`);
    if (data.rows.length === 0) break;
    const lastSeq = data.rows[data.rows.length - 1]!.seq;
    if (lastSeq <= cursor) {
      // A well-behaved server always returns rows with seq > afterSeq; a page that doesn't
      // advance the cursor would otherwise spin this loop forever.
      throw AzoaCliError.network(`Audit page did not advance past seq ${cursor} — refusing to loop.`);
    }
    rows.push(...data.rows.filter((row) => row.seq <= throughSeq));
    cursor = lastSeq;
    if (data.rows.length < MAX_AUDIT_PAGE_SIZE) break;
  }
  return rows;
}

/**
 * Recomputes the full chain locally from the last pin (plan.md §8 H5) and
 * cross-checks both the recompute and the server's own `/audit/verify`
 * before advancing the pin. Any disagreement is a loud, non-zero-exit alert
 * — this command never silently re-pins over a broken chain.
 */
export async function runAuditVerify(options: AuditVerifyOptions): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const client = httpClientFor(resolved);
  const pin = loadAuditPin(resolved.name);

  // lane A1 §contract-deviations #14: fromSeq defaults to the pin so the server-side verify stays
  // incremental too; --full ignores the pin and asks the server to re-walk the whole chain
  // (rate-limited server-side, surfaced as RATE_LIMITED -> exit 5 via errors.ts).
  const fromSeq = options.full ? undefined : options.fromSeq ?? pin?.seq;
  const query = new URLSearchParams();
  if (fromSeq !== undefined) query.set("fromSeq", String(fromSeq));
  if (options.full) query.set("full", "true");
  const queryString = query.toString();
  const { data: serverVerify } = await client.call<VerifyEndpointResponse>(
    "GET",
    `/api/node-admin/audit/verify${queryString ? `?${queryString}` : ""}`,
  );
  if (!serverVerify.ok) {
    throw AzoaCliError.auditIntegrity(
      `AUDIT INTEGRITY ALERT: server-side /audit/verify reported a broken chain at seq ${serverVerify.firstBrokenSeq}.`,
    );
  }

  const serverHead: PinnedState = { seq: serverVerify.verifiedThroughSeq, hash: serverVerify.headHash };

  const rows = await fetchRowsSince(client, options.full ? 0 : pin?.seq ?? 0, serverHead.seq);
  const local = verifyChain(rows, options.full ? null : pin);
  if (!local.ok) {
    throw AzoaCliError.auditIntegrity(
      `AUDIT INTEGRITY ALERT: local recompute detected a broken chain at seq ${local.firstBrokenSeq} ` +
        `(profile '${resolved.name}'). Do not trust this node's audit log until investigated.`,
    );
  }

  // M2: the server's own verify endpoint and our independent local recompute must agree on where
  // the chain actually is — a server that passes its own (possibly compromised) verify check but
  // disagrees with what we just recomputed from the raw rows is exactly the scenario this guards.
  if (local.verifiedThroughSeq !== serverVerify.verifiedThroughSeq || local.headHash !== serverVerify.headHash) {
    throw AzoaCliError.auditIntegrity(
      `AUDIT INTEGRITY ALERT: local recompute (seq ${local.verifiedThroughSeq}, hash ${local.headHash}) disagrees with ` +
        `the server's /audit/verify (seq ${serverVerify.verifiedThroughSeq}, hash ${serverVerify.headHash}).`,
    );
  }

  // M2: the server reporting the SAME seq we already pinned must report the SAME hash for it —
  // a changed hash at an unchanged seq is tampering that a seq-regression check alone would miss.
  if (pin && serverHead.seq === pin.seq && serverHead.hash !== pin.hash) {
    throw AzoaCliError.auditIntegrity(
      `AUDIT INTEGRITY ALERT: server head at seq ${pin.seq} now has hash '${serverHead.hash}', but the pinned hash ` +
        `is '${pin.hash}' — this seq's content changed without the chain advancing.`,
    );
  }

  if (headRegressed(pin, serverHead)) {
    throw AzoaCliError.auditIntegrity(
      `AUDIT INTEGRITY ALERT: audit head regressed — pinned seq ${pin!.seq} but server now reports ${serverHead.seq}.`,
    );
  }

  const newPin: PinnedState = rows.length > 0 ? { seq: local.verifiedThroughSeq, hash: local.headHash } : pin ?? serverHead;
  saveAuditPin(resolved.name, newPin);

  emit(
    options.json,
    `Audit chain verified through seq ${newPin.seq}. Pin updated for profile '${resolved.name}'.`,
    { ok: true, pin: newPin },
  );
}
