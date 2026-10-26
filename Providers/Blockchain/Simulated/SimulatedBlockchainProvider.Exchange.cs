using System.Collections.Concurrent;
using System.Text;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Simulated;

/// <summary>Deterministic, network-free atomic exchange group; see Providers/Blockchain/AGENTS.md §simulated-exchange.</summary>
public sealed partial class SimulatedBlockchainProvider : IExchangeTransactionModule
{
    /// <summary>Prefix on a simulated exchange group id (<c>sim:group:&lt;digest&gt;</c>).</summary>
    public const string SimGroupPrefix = SimPrefix + "group:";

    /// <summary>Finalized position reported for every confirmed simulated group (the store requires a positive one).</summary>
    public const string SimFinalizedPosition = "1";

    // Applied groups (preparationId → groupId); in-process only, like the balance ledger.
    private readonly ConcurrentDictionary<string, string> _exchangeSubmissions = new(StringComparer.Ordinal);
    private readonly object _exchangeGate = new();

    /// <inheritdoc cref="IExchangeTransactionModule.PrepareExchangeAsync"/>
    public Task<AZOAResult<PreparedExchangeWire>> PrepareExchangeAsync(ExchangeWireIntent intent, CancellationToken ct = default)
        => Task.FromResult(intent is not null && ValidSimExchangeIntent(intent)
            ? AZOAResult<PreparedExchangeWire>.Success(ExpectedSimExchange(intent))
            : AZOAResult<PreparedExchangeWire>.Failure("Exact simulated exchange intent on this network is required."));

    /// <inheritdoc cref="IExchangeTransactionModule.SubmitExchangeAsync"/>
    public Task SubmitExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default)
    {
        if (!IsExpectedSimExchange(prepared))
            throw new InvalidOperationException("Exchange wire is not its deterministic simulated candidate.");
        // Atomic group: every member's delta is computed before any is applied, and the group applies at most once.
        var deltas = SimExchangeDeltas(prepared.Intent);
        lock (_exchangeGate)
        {
            if (!_exchangeSubmissions.TryAdd(prepared.PreparationId, prepared.GroupId)) return Task.CompletedTask;
            foreach (var (address, tokenId, delta) in deltas)
                _ledger.AddOrUpdate((address, tokenId), delta, (_, current) => current + delta);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="IExchangeTransactionModule.ObserveExchangeAsync"/>
    public Task<AZOAResult<ExchangeWireObservation>> ObserveExchangeAsync(PreparedExchangeWire prepared, CancellationToken ct = default)
    {
        if (!IsExpectedSimExchange(prepared))
            return Task.FromResult(AZOAResult<ExchangeWireObservation>.Failure("Exchange wire is not its deterministic simulated candidate."));
        var applied = _exchangeSubmissions.TryGetValue(prepared.PreparationId, out var group) && group == prepared.GroupId;
        return Task.FromResult(AZOAResult<ExchangeWireObservation>.Success(new(applied ? "Confirmed" : "Unknown",
            prepared.PreparationId, prepared.GroupId, CanonicalJson.Hash(prepared), prepared.Intent.ProtocolIdentity,
            applied ? SimFinalizedPosition : null)));
    }

    /// <summary>Signed ledger deltas of the whole group in member order: readiness funding, then each leg.</summary>
    private static IReadOnlyList<(string Address, string TokenId, long Delta)> SimExchangeDeltas(ExchangeWireIntent intent)
    {
        var deltas = new List<(string, string, long)>();
        var funding = intent.Readiness is { } ready ? UInt64Text.Parse(ready.FundingBaseUnits) : 0UL;
        if (intent.Readiness is { } readiness && funding > 0)
        {
            deltas.Add((intent.FeePayerAddress, string.Empty, -checked((long)funding)));
            deltas.Add((readiness.Recipient, string.Empty, checked((long)funding)));
        }
        foreach (var leg in intent.Legs)
        {
            var quantity = checked((long)UInt64Text.Parse(leg.QuantityBaseUnits));
            deltas.Add((leg.Sender, leg.AssetId, -quantity));
            deltas.Add((leg.Recipient, leg.AssetId, quantity));
        }
        return deltas;
    }

    /// <summary>Member count mirrors the Algorand group: readiness funding and acceptance, the legs, then the pooled fee payer.</summary>
    private static int SimExchangeMemberCount(ExchangeWireIntent intent)
        => intent.Legs.Count + 1 + (intent.Readiness is null ? 0
            : UInt64Text.Parse(intent.Readiness.FundingBaseUnits) > 0 ? 2 : 1);

    private static PreparedExchangeWire ExpectedSimExchange(ExchangeWireIntent intent)
    {
        var preparationId = CanonicalJson.Hash(new
        {
            Domain = "simulated-exchange-wire-v1", intent.ChainType, intent.Network, intent.SourceId, intent.Generation,
        });
        var intentHash = CanonicalJson.Hash(intent);
        var groupId = SimGroupPrefix + Base32(Sha256(Canonical("exchange-group", preparationId, intentHash)));
        var transactionIds = Enumerable.Range(0, SimExchangeMemberCount(intent))
            .Select(index => SimTxPrefix + Base32(Sha256(Canonical("exchange-member", preparationId, intentHash, index.ToString()))))
            .ToArray();
        var signed = transactionIds.Select(id => Convert.ToBase64String(Encoding.UTF8.GetBytes(Canonical("sim-signed:", id)))).ToArray();
        return new PreparedExchangeWire(intent, preparationId, groupId, transactionIds, signed);
    }

    // Field-wise: records with collections compare by reference, so re-derive and compare each identity.
    private bool IsExpectedSimExchange(PreparedExchangeWire? prepared)
    {
        if (prepared?.Intent is null || !ValidSimExchangeIntent(prepared.Intent)) return false;
        var expected = ExpectedSimExchange(prepared.Intent);
        return expected.PreparationId == prepared.PreparationId && expected.GroupId == prepared.GroupId
            && expected.TransactionIds.SequenceEqual(prepared.TransactionIds, StringComparer.Ordinal)
            && expected.SignedTransactionsBase64.SequenceEqual(prepared.SignedTransactionsBase64, StringComparer.Ordinal);
    }

    /// <summary>The Algorand intent contract (roles, scopes, group size) over sim addresses, bound to <see cref="SimProtocolIdentity"/>.</summary>
    private bool ValidSimExchangeIntent(ExchangeWireIntent intent)
        => intent is { Version: 1, Generation: >= 0 and <= int.MaxValue, Legs.Count: >= 1 and <= 15 }
            && intent.Legs.Count + 1 + (intent.Readiness is null ? 0 : 2) <= 16
            && intent.ChainType == ChainType && intent.Network == ActiveNetwork && intent.ProtocolIdentity == SimProtocolIdentity
            && !string.IsNullOrWhiteSpace(intent.SourceId) && intent.SourceId.Length <= 200
            && IsSimulated(intent.FeePayerAddress) && intent.FeePayerContext.IsPlatform
            && intent.FeePayerContext.ChainType == ChainType
            && UInt64Text.TryParse(intent.MaximumNetworkFeeBaseUnits, out var fee) && fee > 0
            && (intent.Readiness is null || ValidSimExchangeReadiness(intent, intent.Readiness))
            && intent.Legs.All(leg => leg is not null && leg.SigningContext.ChainType == ChainType
                && IsSimulated(leg.Sender) && IsSimulated(leg.Recipient) && leg.Sender != leg.Recipient
                && UInt64Text.TryParse(leg.AssetId, out var asset) && asset > 0
                && UInt64Text.TryParse(leg.QuantityBaseUnits, out var amount) && amount is > 0 and <= long.MaxValue
                && (leg.SignerRole == "Participant" && leg.SigningContext.IsResolvableUserContext && !leg.SigningContext.IsPlatform
                    && leg.SigningContext.Scope == AzoaScopes.SwapSign
                    || leg.SignerRole == "Reserve" && leg.SigningContext.IsPlatform && leg.Sender == intent.FeePayerAddress
                    || leg.SignerRole == "Treasury" && leg.SigningContext.IsPlatform && intent.Treasury is { TenantUserId: var tenant }
                        && tenant != Guid.Empty && !string.IsNullOrWhiteSpace(intent.Treasury.ProjectReference)));

    private bool ValidSimExchangeReadiness(ExchangeWireIntent intent, ExchangeWireReadiness readiness)
        => IsSimulated(readiness.Recipient) && readiness.Recipient != intent.FeePayerAddress
            && UInt64Text.TryParse(readiness.AssetId, out var asset) && asset > 0
            && UInt64Text.TryParse(readiness.FundingBaseUnits, out var funding) && funding <= long.MaxValue
            && readiness.RecipientContext.ChainType == ChainType && readiness.RecipientContext.IsResolvableUserContext
            && !readiness.RecipientContext.IsPlatform && readiness.RecipientContext.Scope == AzoaScopes.SwapSign
            && intent.Legs.Any(leg => leg.SignerRole == "Participant" && leg.Sender == readiness.Recipient
                && leg.SigningContext == readiness.RecipientContext);
}
