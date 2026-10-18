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
  verifiedThroughSeq: number;
  headHash: string;
  firstBrokenSeq?: number;
}

/**
 * Recomputes the full chain locally from the last pin (plan.md §8 H5) and
 * cross-checks both the recompute and the server's own `/audit/verify`
 * before advancing the pin. Any disagreement is a loud, non-zero-exit alert
 * — this command never silently re-pins over a broken chain.
 */
export async function runAuditVerify(options: { profile?: string; json: boolean }): Promise<void> {
  const resolved = resolveProfile(options.profile);
  requireSession(resolved);
  const client = httpClientFor(resolved);
  const pin = loadAuditPin(resolved.name);

  const rows: AuditRow[] = [];
  let afterSeq = pin?.seq ?? 0;
  for (;;) {
    const { data } = await client.call<AuditPage>("GET", `/api/node-admin/audit?afterSeq=${afterSeq}&limit=500`);
    if (data.rows.length === 0) break;
    rows.push(...data.rows);
    afterSeq = data.rows[data.rows.length - 1]!.seq;
    if (data.rows.length < 500) break;
  }

  const local = verifyChain(rows, pin);
  if (!local.ok) {
    throw AzoaCliError.auditIntegrity(
      `AUDIT INTEGRITY ALERT: local recompute detected a broken chain at seq ${local.firstBrokenSeq} ` +
        `(profile '${resolved.name}'). Do not trust this node's audit log until investigated.`,
    );
  }

  const { data: serverVerify } = await client.call<VerifyEndpointResponse>("GET", "/api/node-admin/audit/verify");
  if (!serverVerify.ok) {
    throw AzoaCliError.auditIntegrity(
      `AUDIT INTEGRITY ALERT: server-side /audit/verify reported a broken chain at seq ${serverVerify.firstBrokenSeq}.`,
    );
  }

  const serverHead: PinnedState = { seq: serverVerify.verifiedThroughSeq, hash: serverVerify.headHash };
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
