namespace AZOA.WebAPI.Tests.Conformance;

/// <summary>The invariants FR-6 requires a route to satisfy before it can be enabled.</summary>
public static class ConformanceInvariant
{
    /// <summary>No method returns Ok with a synthesized id. Ids come from the chain or the signer.</summary>
    public const string NoFabricatedSuccess = "C1";

    /// <summary>A status/confirmation read for an unknown transaction answers Unknown, never Confirmed.</summary>
    public const string UnknownIsNeverConfirmed = "C2";

    /// <summary>A signed transaction is never automatically resubmitted.</summary>
    public const string BroadcastIsNotRetried = "C3";

    /// <summary>A confirmation timeout is success carrying a pending marker plus the real id.</summary>
    public const string ConfirmationTimeoutIsNotFailure = "C4";

    /// <summary>A per-user signing context never falls back to a platform key.</summary>
    public const string PerUserSigningNeverFallsBack = "C5";

    /// <summary>Amounts round-trip through RailAmount without truncation, past the old ulong ceiling.</summary>
    public const string AmountsRoundTripWithoutTruncation = "C6";

    /// <summary>A provider without a capability module exposes no method that could fabricate it.</summary>
    public const string CapabilityHonesty = "C7";

    /// <summary>Fail-closed errors name the real cause and never misattribute.</summary>
    public const string FailClosedErrorsNameTheCause = "C8";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        NoFabricatedSuccess,
        UnknownIsNeverConfirmed,
        BroadcastIsNotRetried,
        ConfirmationTimeoutIsNotFailure,
        PerUserSigningNeverFallsBack,
        AmountsRoundTripWithoutTruncation,
        CapabilityHonesty,
        FailClosedErrorsNameTheCause,
    };

    public static string Describe(string id) => id switch
    {
        NoFabricatedSuccess => "no fabricated success",
        UnknownIsNeverConfirmed => "unknown is never confirmed",
        BroadcastIsNotRetried => "broadcast is not retried",
        ConfirmationTimeoutIsNotFailure => "confirmation timeout is not failure",
        PerUserSigningNeverFallsBack => "per-user signing never falls back to a platform key",
        AmountsRoundTripWithoutTruncation => "amounts round-trip without truncation",
        CapabilityHonesty => "capability honesty",
        FailClosedErrorsNameTheCause => "fail-closed errors name the real cause",
        _ => id,
    };
}

/// <summary>How one provider satisfies one invariant. Every pairing must carry a verdict.</summary>
public enum ConformanceVerdict
{
    /// <summary>The shared battery proves it directly, against this provider.</summary>
    Certified,

    /// <summary>
    /// The state cannot be reached without chain-specific fixtures (suggested params, a
    /// blockhash, a nonce), which belong in that chain's own suite. The battery still runs
    /// what it can; the named suite carries the decisive evidence.
    /// </summary>
    CertifiedElsewhere,

    /// <summary>
    /// The provider structurally cannot exhibit the risk. Requires a compensating control
    /// that the battery asserts, so the exemption is itself under test.
    /// </summary>
    NotApplicable,
}

/// <summary>A declared departure from direct certification. Never a silent skip.</summary>
public sealed record ConformanceExemption(
    string ChainType,
    string InvariantId,
    ConformanceVerdict Verdict,
    string Reason,
    string Evidence);

/// <summary>
/// The certification ledger. Any (provider, invariant) pairing absent from here MUST be
/// certified directly by the battery; any pairing present here is reported with its reason
/// and its compensating evidence. Adding a provider adds eight pairings, all of which
/// default to "must be certified directly" until someone writes down why not.
/// </summary>
public static class ProviderConformanceExemptions
{
    public static IReadOnlyList<ConformanceExemption> All { get; } = new[]
    {
        // ─── Simulated: deliberately synthetic, and the only provider that is ───

        new ConformanceExemption(
            "Simulated", ConformanceInvariant.NoFabricatedSuccess, ConformanceVerdict.NotApplicable,
            "The provider exists to synthesize results; there is no chain for an identifier to come from.",
            "Compensating control asserted by the battery: every identifier it returns carries the reserved "
            + "'sim:' marker, and any transaction hash without that marker is refused — so no real chain "
            + "identifier can ever be reported confirmed by it, and no simulated identifier can be mistaken "
            + "for chain truth."),

        new ConformanceExemption(
            "Simulated", ConformanceInvariant.BroadcastIsNotRetried, ConformanceVerdict.NotApplicable,
            "There is no broadcast: the provider has no transport and no signer.",
            "Compensating control asserted by the battery: it issues zero outbound requests across the "
            + "entire battery, and its only constructor accepts no HttpMessageHandler."),

        new ConformanceExemption(
            "Simulated", ConformanceInvariant.ConfirmationTimeoutIsNotFailure, ConformanceVerdict.NotApplicable,
            "Settlement is a synchronous in-memory ledger write; nothing can time out.",
            "Compensating control asserted by the battery: it never emits the pending-confirmation marker, "
            + "so no caller can read a simulated result as 'submitted but unsettled'."),

        new ConformanceExemption(
            "Simulated", ConformanceInvariant.PerUserSigningNeverFallsBack, ConformanceVerdict.NotApplicable,
            "It holds no keys and resolves no signer, so there is no platform key to fall back to.",
            "Compensating control asserted by the battery: its constructor set accepts no "
            + "ITransactionSignerFactory, so no signing path exists to mis-route."),

        // ─── Real chains: the broadcast/confirm states need chain-specific fixtures ───

        new ConformanceExemption(
            "Algorand", ConformanceInvariant.BroadcastIsNotRetried, ConformanceVerdict.CertifiedElsewhere,
            "Reaching a broadcast requires real suggested params from algod, which is a chain-specific "
            + "fixture and does not belong in the shared battery.",
            "The battery certifies the shared non-retrying broadcast seam (RetrySafety.Broadcast) directly, "
            + "and asserts the signed payload never appears twice on the wire whenever signing is reached. "
            + "Decisive evidence: tests/AZOA.WebAPI.Tests/Signing/BridgePrimitivesTests.cs and "
            + "AlgorandProviderTransactTests.cs."),

        new ConformanceExemption(
            "Solana", ConformanceInvariant.BroadcastIsNotRetried, ConformanceVerdict.CertifiedElsewhere,
            "Reaching a broadcast requires a recent blockhash and token-account state from the RPC.",
            "Shared broadcast seam certified here. Decisive evidence: "
            + "tests/AZOA.WebAPI.Tests/Signing/SolanaProviderSplPipelineTests.cs "
            + "'Transfer_broadcast_failure_is_not_retried_double_spend_guard'."),

        new ConformanceExemption(
            "Ethereum", ConformanceInvariant.BroadcastIsNotRetried, ConformanceVerdict.CertifiedElsewhere,
            "Reaching a broadcast requires a nonce, a fee market reading and a gas estimate from the node.",
            "Shared broadcast seam certified here. Decisive evidence: "
            + "tests/AZOA.WebAPI.Tests/Signing/EthereumProviderPipelineTests.cs."),

        new ConformanceExemption(
            "Algorand", ConformanceInvariant.ConfirmationTimeoutIsNotFailure, ConformanceVerdict.CertifiedElsewhere,
            "A confirmation timeout only exists after a successful submit, which needs chain fixtures.",
            "The battery asserts this provider carries the pending-confirmation marker contract. Decisive "
            + "evidence: tests/AZOA.WebAPI.Tests/Signing/AlgorandProviderTransactTests.cs."),

        new ConformanceExemption(
            "Solana", ConformanceInvariant.ConfirmationTimeoutIsNotFailure, ConformanceVerdict.CertifiedElsewhere,
            "A confirmation timeout only exists after a successful submit, which needs chain fixtures.",
            "The battery asserts the pending-confirmation marker contract. Decisive evidence: "
            + "SolanaProviderSplPipelineTests 'Transfer_confirmation_timeout_records_pending_not_failure' and "
            + "'Mint_confirmation_timeout_returns_the_real_signature_under_the_pending_marker'."),

        new ConformanceExemption(
            "Ethereum", ConformanceInvariant.ConfirmationTimeoutIsNotFailure, ConformanceVerdict.CertifiedElsewhere,
            "A confirmation timeout only exists after a successful submit, which needs chain fixtures.",
            "The battery asserts the pending-confirmation marker contract. Decisive evidence: "
            + "tests/AZOA.WebAPI.Tests/Signing/EthereumProviderPipelineTests.cs."),

        new ConformanceExemption(
            "Algorand", ConformanceInvariant.PerUserSigningNeverFallsBack, ConformanceVerdict.CertifiedElsewhere,
            "The shared transport refuses every read, so the pipeline fails before key resolution and a "
            + "direct assertion here would be vacuous.",
            "The battery still asserts no success and no signature for a per-user context with no custody "
            + "seam wired. Decisive evidence: "
            + "tests/AZOA.WebAPI.Tests/Signing/AlgorandProviderCustodySigningTests.cs."),

        new ConformanceExemption(
            "Solana", ConformanceInvariant.PerUserSigningNeverFallsBack, ConformanceVerdict.CertifiedElsewhere,
            "The shared transport refuses every read, so the pipeline fails before key resolution.",
            "The battery still asserts no success and no signature. Decisive evidence: "
            + "SolanaProviderSplPipelineTests "
            + "'Transfer_per_user_context_without_custody_never_falls_back_to_the_platform_key'."),

        new ConformanceExemption(
            "Ethereum", ConformanceInvariant.PerUserSigningNeverFallsBack, ConformanceVerdict.CertifiedElsewhere,
            "The shared transport refuses every read, so the pipeline fails before key resolution.",
            "The battery still asserts no success and no signature. Decisive evidence: "
            + "tests/AZOA.WebAPI.Tests/Signing/EthereumProviderPipelineTests.cs."),
    };

    public static bool TryGet(string chainType, string invariantId, out ConformanceExemption exemption)
    {
        exemption = All.FirstOrDefault(
            e => string.Equals(e.ChainType, chainType, StringComparison.OrdinalIgnoreCase)
                 && e.InvariantId == invariantId)!;
        return exemption is not null;
    }

    public static ConformanceVerdict VerdictFor(string chainType, string invariantId) =>
        TryGet(chainType, invariantId, out var exemption)
            ? exemption.Verdict
            : ConformanceVerdict.Certified;
}
