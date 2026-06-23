using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Core.Blockchain;

/// <summary>
/// Maps the provider-inconsistent <c>GetTransactionStatusAsync</c> dictionary to
/// a normalized <see cref="ChainConfirmation"/> verdict. This is the promoted,
/// shared form of the private <c>ClassifyTx</c> that previously lived inside
/// <c>ReconciliationService</c> (blockchain-recovery-and-portable-wallets §1.1),
/// so the reconciler and the quest engine share ONE conservative classifier.
///
/// <para><b>Conservative invariant (unchanged from the reconciler):</b> a
/// not-found tx / RPC error surfaces as <c>AZOAResult.IsError == true</c> on
/// both Algorand and Solana and is AMBIGUOUS — it is mapped to
/// <see cref="ChainConfirmation.Unknown"/>, NEVER <see cref="ChainConfirmation.FailedOnChain"/>.
/// Only an explicit on-chain negative signal yields FailedOnChain. This is what
/// keeps reconcile-before-retry from re-broadcasting a tx that actually landed.</para>
/// </summary>
public static class ChainTxClassifier
{
    /// <summary>
    /// Classify a <c>GetTransactionStatusAsync</c> result. The base provider
    /// default for <c>GetTransactionConfirmationAsync</c> delegates here; the
    /// reconciler's <c>ProbeChainAsync</c> consumes the same logic.
    /// </summary>
    public static ChainConfirmation Classify(AZOAResult<Dictionary<string, object>> result)
    {
        // IsError ⇒ tx not found / RPC error / not-yet-mined. Ambiguous by
        // construction. NEVER treat as failure.
        if (result.IsError || result.Result is null)
            return ChainConfirmation.Unknown;

        var d = result.Result;

        // Positive confirmation signals (provider-specific keys).
        if (ReadBool(d, "confirmed") == true) return ChainConfirmation.Confirmed; // Algorand
        if (ReadBool(d, "success") == true) return ChainConfirmation.Confirmed;   // Solana

        // Explicit on-chain negative: the tx exists on-chain but failed.
        if (ReadBool(d, "success") == false) return ChainConfirmation.FailedOnChain; // Solana revert

        // Algorand: confirmed==false means 0 rounds = observed-but-not-yet, which
        // is a genuine in-flight signal — Pending, NOT a failure and NOT Unknown.
        // (The dictionary was non-error, so the tx WAS found; it just hasn't
        // reached a confirming round.)
        if (ReadBool(d, "confirmed") == false) return ChainConfirmation.Pending;

        // A non-error result with no recognizable signal: do not guess.
        return ChainConfirmation.Unknown;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var p) => p,
            _ => null,
        };
    }
}
