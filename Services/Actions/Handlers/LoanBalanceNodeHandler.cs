// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Lending;

namespace AZOA.WebAPI.Models.Actions
{
    /// <summary>
    /// LoanBalance config: the tracker to project. Read-only node — see
    /// <c>Services/Actions/AGENTS.md</c> §loan-tracker.
    /// </summary>
    public class LoanBalanceNodeConfig
    {
        /// <summary>The loan tracker holon to project.</summary>
        public Guid LoanHolonId { get; set; }
    }
}

namespace AZOA.WebAPI.Services.Actions.Handlers
{
    /// <summary>
    /// Handles <see cref="ActionNodeType.LoanBalance"/> — Tier-1: projects a loan tracker's
    /// ledger into <c>{ loanId, principal, totalRepaid, totalWrittenOff, outstanding,
    /// status, entryCount }</c>. Pure DB read; writes nothing and moves no value.
    /// </summary>
    /// <remarks>
    /// The arithmetic lives in <see cref="LoanBalanceProjection"/> — the one place a loan
    /// balance is computed — so a gate reading this node's output and a repayment node
    /// checking the outstanding balance can never disagree.
    /// </remarks>
    public sealed class LoanBalanceNodeHandler : IActionNodeHandler
    {
        private readonly IHolonManager _holonManager;

        public LoanBalanceNodeHandler(IHolonManager holonManager) => _holonManager = holonManager;

        public ActionNodeType NodeType => ActionNodeType.LoanBalance;

        // RequiresChainCapability intentionally NOT overridden — Tier-1, chain-free.

        public async Task<ActionNodeHandlerResult> HandleAsync(
            ActionNodeExecutionContext context, CancellationToken ct = default)
        {
            if (!ActionNodeConfig.TryDeserialize<LoanBalanceNodeConfig>(
                    context.Node.Config, nameof(ActionNodeType.LoanBalance), out var cfg, out var cfgError))
                return ActionNodeResults.Fail(cfgError);

            if (cfg.LoanHolonId == Guid.Empty)
                return ActionNodeResults.Invalid("[LoanBalance] loanHolonId is required.");

            var (snapshot, readError) = await LoanLedgerReader.TryLoadAsync(
                _holonManager, cfg.LoanHolonId, context.ActingUserId);
            if (snapshot is null)
                return ActionNodeResults.Fail($"[LoanBalance] {readError}");

            try
            {
                var projection = LoanBalanceProjection.Project(snapshot.Loan, snapshot.Entries);
                return ActionNodeResults.Ok(JsonSerializer.Serialize(projection, ActionNodeJson.Options));
            }
            catch (LoanLedgerFormatException ex)
            {
                return ActionNodeResults.Fail($"[LoanBalance] {ex.Message}");
            }
        }
    }
}
