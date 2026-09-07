// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Lending;

namespace AZOA.WebAPI.Models.Actions
{
    /// <summary>
    /// LoanRepaymentRecord config: which tracker, how much came back, and whether the row
    /// is a repayment or a write-off. The unit is NOT restated here — it is read from the
    /// tracker, so a repayment can never be recorded in a different currency than the loan.
    /// See <c>Services/Actions/AGENTS.md</c> §loan-tracker.
    /// </summary>
    public class LoanRepaymentRecordNodeConfig
    {
        /// <summary>The loan tracker holon this row belongs to.</summary>
        public Guid LoanHolonId { get; set; }

        /// <summary>Amount repaid or written off, as a base-unit decimal string (D13).</summary>
        public string Amount { get; set; } = string.Empty;

        /// <summary><c>Repayment</c> (default) or <c>WriteOff</c>. <c>Disbursement</c> is rejected — LoanOpen writes that row.</summary>
        public string? EntryKind { get; set; }

        /// <summary>
        /// The tenant's own reference for the value movement this row records (e.g. the
        /// upstream Transfer's tx hash). Optional, and a SECONDARY replay key: re-recording
        /// the same reference against the same tracker is a no-op. Replay protection does not
        /// depend on it — the run+node execution key always does that job.
        /// </summary>
        public string? Reference { get; set; }
    }
}

namespace AZOA.WebAPI.Services.Actions.Handlers
{
    /// <summary>
    /// Handles <see cref="ActionNodeType.LoanRepaymentRecord"/> — Tier-1: appends one
    /// repayment or write-off row to a loan tracker's ledger. Moves no value.
    /// </summary>
    /// <remarks>
    /// <para>The value itself moved through an upstream Tier-2 <c>Transfer</c> the tenant
    /// composed; this node records the fact. Chain-free, so <c>RequiresChainCapability</c>
    /// stays the SPI default false.</para>
    /// <para><b>Partial repayment and write-off are ordinary states, not errors</b> (D21):
    /// a repayment below the outstanding balance returns Ok and leaves the tracker
    /// <c>Open</c>, and so does a write-off that leaves part of the principal still
    /// collectible. Only when the balance reaches zero does the tracker close — as
    /// <c>WrittenOff</c> if anything was abandoned to get there, else <c>Repaid</c>. The one
    /// genuine error is over-repayment — it means the ledger disagrees with reality, so the
    /// row is refused rather than clamped. A tracker whose outstanding balance has already
    /// reached zero therefore refuses every further row: reopening a closed loan is a
    /// deliberate reconciliation, not a repayment.</para>
    /// <para><b>Replay protection is unconditional</b>, because the durable engine retries by
    /// design. Every row this node writes is stamped with the <c>runId:nodeId</c> of the
    /// execution that wrote it (the Bridge/Back/Swap idempotency seed), and a retry that finds
    /// its own stamp appends nothing. The tenant's optional <c>reference</c> adds a second,
    /// tenant-level identity on top; neither guard ever inspects the DISBURSEMENT row, which
    /// <c>LoanOpen</c> stamps with the tenant's reference and which would otherwise swallow
    /// the first legitimate repayment carrying that same reference.</para>
    /// <para>The read, the guard and the append run under
    /// <see cref="LoanLedgerWriteGate"/> so two concurrent branches cannot both pass an
    /// over-repayment check computed from the same stale balance.</para>
    /// </remarks>
    public sealed class LoanRepaymentRecordNodeHandler : IActionNodeHandler
    {
        private readonly IHolonManager _holonManager;

        public LoanRepaymentRecordNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

        public ActionNodeType NodeType => ActionNodeType.LoanRepaymentRecord;

        // RequiresChainCapability intentionally NOT overridden — Tier-1, chain-free.

        public async Task<ActionNodeHandlerResult> HandleAsync(
            ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            if (!ActionNodeConfig.TryDeserialize<LoanRepaymentRecordNodeConfig>(
                    context.Node.Config, nameof(ActionNodeType.LoanRepaymentRecord), out var cfg, out var cfgError))
                return ActionNodeResults.Fail(cfgError);

            if (cfg.LoanHolonId == Guid.Empty)
                return ActionNodeResults.Invalid("[LoanRepaymentRecord] loanHolonId is required.");

            var kind = LoanLedgerEntryKind.Repayment;
            if (!string.IsNullOrWhiteSpace(cfg.EntryKind)
                && !LoanHolonTypes.TryParseEntryKind(cfg.EntryKind, out kind))
                return ActionNodeResults.Invalid(
                    $"[LoanRepaymentRecord] entryKind '{cfg.EntryKind}' is not a known ledger entry kind.");

            if (kind == LoanLedgerEntryKind.Disbursement)
                return ActionNodeResults.Invalid(
                    "[LoanRepaymentRecord] a disbursement row is written by LoanOpen; this node records repayments and write-offs only.");

            // Everything from here to the append is one step per tracker: the balance this
            // node checks must still be the balance when it writes, or two branches both pass
            // the over-repayment guard and both append.
            using var ledgerLease = await LoanLedgerWriteGate.EnterAsync(cfg.LoanHolonId, ct);

            // Runner-scoped read of the tracker AND its whole ledger (D20 rule).
            var (snapshot, readError) = await LoanLedgerReader.TryLoadAsync(
                _holonManager, cfg.LoanHolonId, context.ActingUserId);
            if (snapshot is null)
                return ActionNodeResults.Fail($"[LoanRepaymentRecord] {readError}");

            LoanBalanceTotals totals;
            try
            {
                totals = LoanBalanceProjection.Compute(snapshot.Loan, snapshot.Entries);
            }
            catch (LoanLedgerFormatException ex)
            {
                return ActionNodeResults.Fail($"[LoanRepaymentRecord] {ex.Message}");
            }

            // Replay guard, unconditional: this execution's own stamp identifies the row it
            // already wrote whether or not the tenant supplied a reference, so an engine
            // retry cannot double-count. The disbursement is never a match.
            var executionKey = BuildExecutionKey(context);
            if (FindAlreadyRecorded(snapshot, executionKey, cfg.Reference) is { } already)
                return ActionNodeResults.Ok(
                    Serialize(LoanBalanceProjection.ToResult(snapshot.Loan.Id, totals)),
                    $"this row is already recorded as ledger entry '{already.Id}'; nothing was appended.");

            if (!RailAmount.TryParse(cfg.Amount, totals.Principal.Dimension, out var amount))
                return ActionNodeResults.Invalid(
                    $"[LoanRepaymentRecord] amount '{cfg.Amount}' is not a base-unit decimal string.");

            if (amount.IsZero)
                return ActionNodeResults.Invalid("[LoanRepaymentRecord] amount must be greater than zero; a zero row records nothing.");

            if (amount.BaseUnits > totals.Outstanding.BaseUnits)
                return ActionNodeResults.Fail(
                    $"[LoanRepaymentRecord] {LoanHolonTypes.ToText(kind)} of {amount.ToBaseUnitString()} exceeds the "
                    + $"outstanding balance {totals.Outstanding.ToBaseUnitString()} of loan '{snapshot.Loan.Id}'; "
                    + "the ledger and the value that actually moved disagree.");

            var entryResult = await _holonManager.CreateAsync(
                BuildEntryModel(cfg, snapshot.Loan.Id, kind, amount, executionKey), context.ActingUserId);
            if (entryResult.IsError || entryResult.Result is not { } entry)
                return ActionNodeResults.Fail(
                    $"[LoanRepaymentRecord] could not append the ledger entry: {entryResult.Message}");

            LoanBalanceTotals updated;
            try
            {
                updated = LoanBalanceProjection.Compute(snapshot.Loan, snapshot.Entries.Append(entry));
            }
            catch (LoanLedgerFormatException ex)
            {
                return ActionNodeResults.Fail($"[LoanRepaymentRecord] ledger entry '{entry.Id}' was written but is not projectable: {ex.Message}");
            }

            var mirrorNote = await MirrorStatusAsync(snapshot.Loan, updated.Status, context.ActingUserId);
            return ActionNodeResults.Ok(Serialize(LoanBalanceProjection.ToResult(snapshot.Loan.Id, updated)), mirrorNote);
        }

        /// <summary>
        /// The stable identity of THIS execution — the same <c>runId:nodeId</c> seed the
        /// Bridge, Back, Swap and FungibleTokenCreate handlers use, because
        /// <see cref="ActionNodeExecutionContext"/> exposes no client-idempotency surface.
        /// A retry of a node re-runs it under the same pair; a genuinely new record is always
        /// a different node or a different run.
        /// </summary>
        private static string BuildExecutionKey(ActionNodeExecutionContext context) =>
            $"{context.RunId}:{context.NodeId}";

        /// <summary>
        /// The row this node already wrote for this movement, if any: its own execution stamp
        /// first, then the tenant's reference.
        /// </summary>
        /// <remarks>
        /// Both identities are matched ONLY against the kinds this node writes. The
        /// disbursement row carries the tenant's reference too (LoanOpen stamps it), so
        /// matching it would silently swallow the first real repayment that cites the
        /// disbursement's reference and report Ok for a row that was never appended.
        /// </remarks>
        private static IHolon? FindAlreadyRecorded(
            LoanLedgerSnapshot snapshot, string executionKey, string? reference)
        {
            foreach (var entry in snapshot.Entries)
            {
                if (!IsRecordedByThisNode(entry))
                    continue;

                if (MetadataMatches(entry, LoanHolonTypes.MetadataKeys.ExecutionKey, executionKey))
                    return entry;

                if (!string.IsNullOrWhiteSpace(reference)
                    && MetadataMatches(entry, LoanHolonTypes.MetadataKeys.Reference, reference!))
                    return entry;
            }

            return null;
        }

        /// <summary>True for the kinds this node appends — a repayment or a write-off, never a disbursement.</summary>
        private static bool IsRecordedByThisNode(IHolon entry) =>
            entry.Metadata.TryGetValue(LoanHolonTypes.MetadataKeys.EntryKind, out var kindText)
            && LoanHolonTypes.TryParseEntryKind(kindText, out var kind)
            && kind != LoanLedgerEntryKind.Disbursement;

        private static bool MetadataMatches(IHolon entry, string key, string value) =>
            entry.Metadata.TryGetValue(key, out var existing)
            && string.Equals(existing, value, StringComparison.Ordinal);

        /// <summary>
        /// Mirrors the DERIVED status onto the tracker so gate predicates can read
        /// <c>holon.&lt;id&gt;.status</c>. Best-effort by design: the ledger rows are the
        /// truth, and failing the node here would invite a retry that records the repayment
        /// twice. A failed mirror comes back as a note on the successful result.
        /// </summary>
        private async Task<string?> MirrorStatusAsync(IHolon loan, LoanStatus status, Guid actingUserId)
        {
            var statusText = LoanHolonTypes.ToText(status);
            if (loan.Metadata.TryGetValue(LoanHolonTypes.MetadataKeys.Status, out var current)
                && string.Equals(current, statusText, StringComparison.Ordinal))
                return null;

            var update = new HolonUpdateModel
            {
                Metadata = new Dictionary<string, string> { [LoanHolonTypes.MetadataKeys.Status] = statusText },
            };

            var result = await _holonManager.UpdateAsync(loan.Id, update, actingUserId);
            return result.IsError
                ? $"the ledger row was recorded but the tracker's status mirror could not be set to '{statusText}': {result.Message}"
                : null;
        }

        private static HolonCreateModel BuildEntryModel(
            LoanRepaymentRecordNodeConfig cfg, Guid loanHolonId, LoanLedgerEntryKind kind,
            RailAmount amount, string executionKey)
        {
            var kindText = LoanHolonTypes.ToText(kind);
            var metadata = new Dictionary<string, string>
            {
                [LoanHolonTypes.MetadataKeys.EntryKind] = kindText,
                [LoanHolonTypes.MetadataKeys.LoanHolonId] = loanHolonId.ToString(),
                [LoanHolonTypes.MetadataKeys.ExecutionKey] = executionKey,
                [LoanHolonTypes.MetadataKeys.RecordedAt] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
            LoanAmountMetadata.Write(metadata, LoanHolonTypes.MetadataKeys.Amount, amount);

            if (!string.IsNullOrWhiteSpace(cfg.Reference))
                metadata[LoanHolonTypes.MetadataKeys.Reference] = cfg.Reference!;

            return new HolonCreateModel
            {
                Name = $"loan-entry-{kindText.ToLowerInvariant()}",
                ParentHolonId = loanHolonId,
                AssetType = LoanHolonTypes.LoanLedgerEntry,
                Metadata = metadata,
            };
        }

        private static string Serialize(LoanBalanceProjectionResult projection) =>
            JsonSerializer.Serialize(projection, ActionNodeJson.Options);
    }
}
