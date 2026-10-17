using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IWttCleanupModule.PrepareWttCleanupAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> PrepareWttCleanupAsync(WttOperationIntent intent,
        WttStageQuote originalQuote, WttTargetProofs? proofs, WttStagePreparation original, WttCleanupQuote quote,
        WttCleanupConsent consent, WttSigningAuthority authority, int generation, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttCleanupQuote(intent, quote);
            if (quote.OriginalQuoteId != originalQuote.QuoteId || quote.OriginalPreparationHash != CanonicalJson.Hash(original)
                || quote.Phase != originalQuote.Phase || quote.OriginalStage != original.Stage || quote.OriginalGeneration != original.Generation
                || authority.Context.IsPlatform || !authority.Context.IsResolvableUserContext
                || authority.Context.UserId != intent.ActorUserId || authority.Context.WalletId != plan.PayerBinding.WalletId
                || authority.Context.ActingTenantId != Guid.Empty || authority.Context.GrantorUserId != Guid.Empty
                || authority.Context.ChainType != ChainType || authority.Context.Scope != AzoaScopes.WttCleanupSign
                || authority.Address != quote.DestinationAddress)
                throw new InvalidDataException("Cleanup requires fresh direct original-owner consent for the exact original stage.");
            var binding = WttCleanupBinding(intent, quote, consent, generation);
            var id = WttPreparationIdentity.Id(binding);
            var bindingJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding));
            var reservation = WttCleanupReservation(binding, quote);
            IReadOnlyList<WttStageKeyBinding> expected = [plan.PayerBinding];
            byte[] Compile(DurableSolanaPlan recovery, IReadOnlyDictionary<string, WttStageKeyBinding> keys)
                => CompileWttCleanupBound(recovery, keys, quote, binding, plan);
            using var scope = _custodyScopeFactory?.CreateScope();
            var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
                ?? throw new InvalidOperationException("Durable WTT cleanup storage is required.");
            var saved = await store.GetAsync(id, ct);
            if (saved is not null && (saved.BindingJson != bindingJson || saved.Sealed))
                throw new InvalidDataException("Cleanup generation was sealed or belongs to another request.");
            if (saved?.SignedPreparationJson is not null)
                return Ok(await ReadWttSolanaWireAsync(saved, binding, reservation, expected, Compile, ct));
            if (quote.ExpiresAt <= DateTimeOffset.UtcNow || UInt64Text.Parse(quote.ReturnAmount) == 0)
                throw new InvalidDataException("Fresh cleanup requires an unexpired nonzero return offer.");
            var current = await QuoteWttCleanupAsync(intent, originalQuote, proofs, original, ct);
            if (current.IsError || current.Result is null || current.Result.OriginalObservationHash != quote.OriginalObservationHash
                || current.Result.StageAccount != quote.StageAccount || current.Result.DestinationAddress != quote.DestinationAddress
                || current.Result.OriginalAttributedDepositReturn != quote.OriginalAttributedDepositReturn
                || UInt64Text.Parse(current.Result.ReturnAmount) < UInt64Text.Parse(quote.ReturnAmount)
                || UInt64Text.Parse(current.Result.NetworkFee) > UInt64Text.Parse(quote.NetworkFee))
                throw new InvalidDataException("Original finalized evidence, residual balance or cleanup fee changed before signing.");
            var vault = _wttStageKeyVault ?? scope?.ServiceProvider.GetRequiredService<WttStageKeyVault>()
                ?? throw new InvalidOperationException("Protected WTT cleanup custody is required.");
            if (saved is null)
            {
                var recovery = await NewPlanAsync("", 0, 0, false, binding.IntentHash, ct);
                if ("solana:" + recovery.Genesis != binding.ProtocolIdentity)
                    throw new InvalidDataException("Cleanup generation belongs to another Solana genesis.");
                var keys = new Dictionary<string, WttStageKeyBinding>(StringComparer.Ordinal) { [quote.StageAccount] = plan.PayerBinding };
                var wire = new WttSolanaWirePlan(recovery, Convert.ToBase64String(Compile(recovery, keys)), keys);
                saved = await store.ClaimAsync(new WttPreparation { Id = id, BindingJson = bindingJson,
                    PlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire)),
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow }, ct);
            }
            if (saved.SignedPreparationJson is not null)
                return Ok(await ReadWttSolanaWireAsync(saved, binding, reservation, expected, Compile, ct));
            var winner = ReadWttSolanaPlan(saved, binding, expected, Compile);
            var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
            var message = Convert.FromBase64String(winner.MessageBase64);
            if (await VerifyGenesisAsync(ct) != winner.Recovery.Genesis || height.GetInt64() > winner.Recovery.LastValidBlockHeight
                || await NativeMessageFeeAsync(message, ct) > UInt64Text.Parse(quote.NetworkFee) || quote.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("Cleanup validity or fee changed before custody; retain the original generation.");
            var signed = await SignWttMessageAsync(message, authority, winner.StageKeys, vault, ct, cleanup: true);
            if (signed.IsError || signed.Result is null) return Error<WttStagePreparation>(signed.Message);
            var prepared = new WttStagePreparation(binding.Stage, generation, SolanaMessageEnvelope.VerifyWire(signed.Result, message),
                Convert.ToBase64String(signed.Result), binding.ProtocolIdentity, saved.PlanJson, reservation);
            var committed = await store.TrySaveSignedAsync(saved, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(prepared)), ct)
                ?? await store.GetAsync(id, ct);
            if (committed?.SignedPreparationJson is null || committed.Sealed)
                throw new InvalidDataException("Cleanup generation changed before exact signature persistence.");
            return Ok(await ReadWttSolanaWireAsync(committed, binding, reservation, expected, Compile, ct));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException
            or FormatException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { return Error<WttStagePreparation>(exception.Message); }
    }

    private static WttSolanaCleanupPlan ReadWttCleanupQuote(WttOperationIntent intent, WttCleanupQuote quote)
    {
        if (quote.Version != 1 || quote.IntentHash != CanonicalJson.Hash(intent) || quote.Phase is not ("Source" or "Target")
            || quote.QuoteId != CanonicalJson.Hash(quote with { QuoteId = "" }))
            throw new InvalidDataException("Cleanup offer differs from its canonical original intent.");
        var owner = quote.Phase == "Source" ? intent.SourceAuthority : intent.TargetAuthority;
        var network = quote.Phase == "Source" ? intent.Source : intent.Target;
        var plan = JsonSerializer.Deserialize<WttSolanaCleanupPlan>(quote.PlanJson)
            ?? throw new InvalidDataException("Cleanup evidence is missing.");
        if (plan.Version != 1 || plan.PayerBinding is null || quote.PlanJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan))
            || network.ChainType != "Solana" || quote.ProtocolIdentity != network.ProtocolIdentity
            || quote.DestinationAddress != owner.Address || quote.StageAccount == owner.Address
            || plan.PayerBinding.ActorUserId != intent.ActorUserId || plan.PayerBinding.WalletId != owner.Context.WalletId
            || plan.PayerBinding.OperationId != intent.OperationId || plan.PayerBinding.ProtocolIdentity != quote.ProtocolIdentity
            || plan.PayerBinding.Generation != quote.OriginalGeneration || plan.PayerBinding.TenantId != intent.TenantId
            || plan.PayerBinding.StageRole != quote.Phase + ":" + quote.OriginalStage + ":payer"
            || plan.PayerBinding.SigningScope != (quote.Phase == "Source" ? AzoaScopes.WttSourceSign : AzoaScopes.WttTargetSign)
            || plan.OriginalObservationHash != quote.OriginalObservationHash || plan.SnapshotSlot < plan.OriginalSlot
            || plan.SnapshotBalance != UInt64Text.Parse(quote.ReturnAmount) || plan.SnapshotBalance < plan.OriginalAfter
            || UInt64Text.Parse(quote.OriginalAttributedDepositReturn) > plan.SnapshotBalance)
            throw new InvalidDataException("Cleanup evidence changed original owner, stage, funding or finalized identity.");
        _ = UInt64Text.Parse(quote.NetworkFee);
        return plan;
    }

    private static WttPreparationBinding WttCleanupBinding(WttOperationIntent intent, WttCleanupQuote quote,
        WttCleanupConsent consent, int generation)
    {
        if (consent.Version != 1 || consent.QuoteId != quote.QuoteId || string.IsNullOrWhiteSpace(consent.RequestKey)
            || consent.RequestKey.Length > 200 || UInt64Text.Parse(consent.MaximumNetworkFee) < UInt64Text.Parse(quote.NetworkFee))
            throw new InvalidDataException("Cleanup requires exact explicit cost acceptance and a durable request key.");
        return new(quote.ProtocolIdentity, intent.ActorUserId, null, intent.OperationId, "Cleanup",
            quote.Phase + ":" + quote.OriginalStage + ":" + quote.OriginalGeneration.ToString(CultureInfo.InvariantCulture)
                + ":" + CanonicalJson.Hash(new { consent.RequestKey }), generation,
            CanonicalJson.Hash(new { intent, quote, consent }), quote.QuoteId);
    }

    private static WttStageReservation WttCleanupReservation(WttPreparationBinding binding, WttCleanupQuote quote)
        => new(binding.Stage, quote.NetworkFee, "0", "0", quote.DestinationAddress);

    private static byte[] CompileWttCleanupBound(DurableSolanaPlan recovery, IReadOnlyDictionary<string, WttStageKeyBinding> keys,
        WttCleanupQuote quote, WttPreparationBinding binding, WttSolanaCleanupPlan plan)
    {
        if (keys.Count != 1 || !keys.TryGetValue(quote.StageAccount, out var key) || key != plan.PayerBinding)
            throw new InvalidDataException("Cleanup cannot substitute another protected payer key.");
        return CompileWttCleanup(recovery, quote.DestinationAddress, quote.StageAccount, UInt64Text.Parse(quote.ReturnAmount),
            CanonicalJson.Hash(binding));
    }
}
