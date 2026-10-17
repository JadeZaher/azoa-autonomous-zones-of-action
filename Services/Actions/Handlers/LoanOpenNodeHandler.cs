// SPDX-License-Identifier: UNLICENSED

using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Lending;

namespace AZOA.WebAPI.Models.Actions
{
    /// <summary>
    /// LoanOpen config: the principal that was disbursed, the unit it is counted in, and
    /// descriptive labels. Lives here (not in <c>NodeConfigs.cs</c>) so the whole
    /// loan-tracker lane stays in one place — see <c>Services/Actions/AGENTS.md</c>
    /// §loan-tracker.
    /// </summary>
    /// <remarks>
    /// There is deliberately no field describing the price of time — see the lane rule in
    /// §loan-tracker and <c>NoInterestPrimitiveGuardTests</c>, which fails the build if one
    /// appears.
    /// </remarks>
    public class LoanOpenNodeConfig
    {
        /// <summary>Principal, as a base-unit decimal string (D13 — never a JSON number).</summary>
        public string Principal { get; set; } = string.Empty;

        /// <summary>Asset id (ASA/mint/contract) or ISO-4217 code. Required.</summary>
        public string CurrencyCode { get; set; } = string.Empty;

        /// <summary><c>ChainAsset</c> (default) or <c>FiatCurrency</c>.</summary>
        public string? CurrencyKind { get; set; }

        /// <summary>Display name of the tracker holon.</summary>
        public string? Name { get; set; }

        /// <summary>Display description of the tracker holon.</summary>
        public string? Description { get; set; }

        /// <summary>Optional holarchy parent for the tracker (e.g. a lending programme holon).</summary>
        public Guid? ParentHolonId { get; set; }

        /// <summary>Descriptive label only — no authority is derived from it.</summary>
        public Guid? BorrowerUserId { get; set; }

        /// <summary>Descriptive label only — no authority is derived from it.</summary>
        public Guid? LenderUserId { get; set; }

        /// <summary>The tenant's own reference for the disbursement that already moved the value.</summary>
        public string? Reference { get; set; }
    }
}

namespace AZOA.WebAPI.Services.Actions.Handlers
{
    /// <summary>
    /// Handles <see cref="ActionNodeType.LoanOpen"/> — Tier-1: opens a loan TRACKER and
    /// writes its opening disbursement ledger row. Moves no value.
    /// </summary>
    /// <remarks>
    /// <para>D21: the loan is a Holon (<c>AssetType = LoanTracker</c>) and its ledger is
    /// child Holons (<c>AssetType = LoanLedgerEntry</c>). The money itself moves through the
    /// existing Tier-2 <c>Grant</c>/<c>Transfer</c> nodes the tenant composes upstream; this
    /// node only records that it happened. Nothing here signs, broadcasts, or touches a
    /// wallet, so <c>RequiresChainCapability</c> stays the SPI default false.</para>
    /// <para>The two writes are not atomic: if the ledger row fails after the tracker was
    /// created, the node fails with the tracker id in the message so it can be reconciled —
    /// a tracker with no disbursement row would otherwise project a principal nobody
    /// received.</para>
    /// </remarks>
    public sealed class LoanOpenNodeHandler : IActionNodeHandler
    {
        private readonly IHolonManager _holonManager;

        public LoanOpenNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

        public ActionNodeType NodeType => ActionNodeType.LoanOpen;

        // RequiresChainCapability intentionally NOT overridden — Tier-1, chain-free.

        public async Task<ActionNodeHandlerResult> HandleAsync(
            ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            if (!ActionNodeConfig.TryDeserialize<LoanOpenNodeConfig>(
                    context.Node.Config, nameof(ActionNodeType.LoanOpen), out var cfg, out var cfgError))
                return ActionNodeResults.Invalid(cfgError);

            if (!LoanAmountMetadata.TryBuildDimension(cfg.CurrencyKind, cfg.CurrencyCode, out var dimension, out var unitError))
                return ActionNodeResults.Invalid($"[LoanOpen] {unitError}");

            if (!RailAmount.TryParse(cfg.Principal, dimension, out var principal))
                return ActionNodeResults.Invalid(
                    $"[LoanOpen] principal '{cfg.Principal}' is not a base-unit decimal string.");

            if (principal.IsZero)
                return ActionNodeResults.Invalid("[LoanOpen] principal must be greater than zero; a zero principal tracks nothing.");

            var recordedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            var loanResult = await _holonManager.CreateAsync(
                BuildTrackerModel(cfg, principal, recordedAt), context.ActingUserId);
            if (loanResult.IsError || loanResult.Result is not { } loan)
                return ActionNodeResults.Fail($"[LoanOpen] could not create the loan tracker: {loanResult.Message}");

            var entryResult = await _holonManager.CreateAsync(
                BuildDisbursementModel(
                    cfg, loan.Id, principal, recordedAt, $"{context.RunId}:{context.NodeId}"),
                context.ActingUserId);
            if (entryResult.IsError || entryResult.Result is not { } entry)
                return ActionNodeResults.Fail(
                    $"[LoanOpen] loan tracker '{loan.Id}' was created but its disbursement entry was not: "
                    + $"{entryResult.Message}. Reconcile the tracker before recording repayments against it.");

            try
            {
                var projection = LoanBalanceProjection.Project(loan, new[] { entry });
                return ActionNodeResults.Ok(JsonSerializer.Serialize(projection, ActionNodeJson.Options));
            }
            catch (LoanLedgerFormatException ex)
            {
                return ActionNodeResults.Fail($"[LoanOpen] loan tracker '{loan.Id}' is not projectable: {ex.Message}");
            }
        }

        private static HolonCreateModel BuildTrackerModel(LoanOpenNodeConfig cfg, RailAmount principal, string recordedAt)
        {
            var metadata = new Dictionary<string, string>
            {
                [LoanHolonTypes.MetadataKeys.Status] = LoanHolonTypes.ToText(LoanStatus.Open),
                [LoanHolonTypes.MetadataKeys.RecordedAt] = recordedAt,
            };
            LoanAmountMetadata.Write(metadata, LoanHolonTypes.MetadataKeys.Principal, principal);

            if (cfg.BorrowerUserId is { } borrower)
                metadata[LoanHolonTypes.MetadataKeys.BorrowerUserId] = borrower.ToString();
            if (cfg.LenderUserId is { } lender)
                metadata[LoanHolonTypes.MetadataKeys.LenderUserId] = lender.ToString();
            if (!string.IsNullOrWhiteSpace(cfg.Reference))
                metadata[LoanHolonTypes.MetadataKeys.Reference] = cfg.Reference!;

            return new HolonCreateModel
            {
                Name = string.IsNullOrWhiteSpace(cfg.Name) ? "loan-tracker" : cfg.Name!,
                Description = cfg.Description ?? string.Empty,
                ParentHolonId = cfg.ParentHolonId,
                AssetType = LoanHolonTypes.LoanTracker,
                Metadata = metadata,
            };
        }

        /// <remarks>
        /// The disbursement carries the execution stamp for provenance only. Replay matching
        /// in <c>LoanRepaymentRecordNodeHandler</c> never looks at a disbursement row —
        /// including this one in that search is what let a repayment citing the disbursement's
        /// own <c>reference</c> be swallowed as a replay.
        /// </remarks>
        private static HolonCreateModel BuildDisbursementModel(
            LoanOpenNodeConfig cfg, Guid loanHolonId, RailAmount principal, string recordedAt, string executionKey)
        {
            var metadata = new Dictionary<string, string>
            {
                [LoanHolonTypes.MetadataKeys.EntryKind] = LoanHolonTypes.ToText(LoanLedgerEntryKind.Disbursement),
                [LoanHolonTypes.MetadataKeys.LoanHolonId] = loanHolonId.ToString(),
                [LoanHolonTypes.MetadataKeys.ExecutionKey] = executionKey,
                [LoanHolonTypes.MetadataKeys.RecordedAt] = recordedAt,
            };
            LoanAmountMetadata.Write(metadata, LoanHolonTypes.MetadataKeys.Amount, principal);

            if (!string.IsNullOrWhiteSpace(cfg.Reference))
                metadata[LoanHolonTypes.MetadataKeys.Reference] = cfg.Reference!;

            return new HolonCreateModel
            {
                Name = "loan-entry-disbursement",
                ParentHolonId = loanHolonId,
                AssetType = LoanHolonTypes.LoanLedgerEntry,
                Metadata = metadata,
            };
        }
    }
}
