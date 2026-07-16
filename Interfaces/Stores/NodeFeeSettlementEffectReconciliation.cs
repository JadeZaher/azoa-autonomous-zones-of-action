using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Observed nonterminal outcomes for the paired settlement effects.</summary>
public sealed record NodeFeeSettlementEffectReconciliation(
    NodeFeeSettlement.EffectStateKind PrimaryEffectState,
    string? PrimaryEffectReference,
    NodeFeeSettlement.EffectStateKind FeeEffectState,
    string? FeeEffectReference)
{
    /// <summary>True only for outcomes that require another reconciliation attempt.</summary>
    public bool IsNonTerminal
        => PrimaryEffectState is NodeFeeSettlement.EffectStateKind.Unknown or NodeFeeSettlement.EffectStateKind.Failed
           || FeeEffectState is NodeFeeSettlement.EffectStateKind.Unknown or NodeFeeSettlement.EffectStateKind.Failed;
}
