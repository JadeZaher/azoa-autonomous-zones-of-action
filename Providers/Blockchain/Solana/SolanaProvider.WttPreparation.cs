using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private readonly IWttPreparationStore? _wttPreparationStore;
    private readonly WttStageKeyVault? _wttStageKeyVault;
    private sealed record WttSolanaWirePlan(DurableSolanaPlan Recovery, string MessageBase64,
        IReadOnlyDictionary<string, WttStageKeyBinding> StageKeys);

    private async Task<AZOAResult<WttStagePreparation>> PrepareWttSolanaWireAsync(WttPreparationBinding binding,
        WttStageQuote quote, WttSigningAuthority payer, IReadOnlyList<WttStageKeyBinding> expectedKeys,
        Func<DurableSolanaPlan, IReadOnlyDictionary<string, WttStageKeyBinding>, byte[]> compile,
        Func<CancellationToken, Task> checkCurrentState, CancellationToken ct,
        Func<byte[], CancellationToken, Task>? verifyMessage = null,
        IReadOnlySet<WttStageKeyBinding>? references = null)
    {
        var id = WttPreparationIdentity.Id(binding);
        var bindingJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding));
        if (binding.IntentHash != quote.IntentHash || binding.QuoteId != quote.QuoteId || binding.Phase != quote.Phase
            || binding.ProtocolIdentity != quote.ProtocolIdentity || payer.Context.UserId != binding.ActorUserId
            || payer.Context.ActingTenantId != binding.TenantId.GetValueOrDefault() || payer.Context.ChainType != ChainType
            || payer.Context.IsPlatform || !payer.Context.IsResolvableUserContext
            || expectedKeys.Count == 0 || expectedKeys.Distinct().Count() != expectedKeys.Count
            || expectedKeys.Any(key => key.ProtocolIdentity != binding.ProtocolIdentity || key.ActorUserId != binding.ActorUserId
                || key.TenantId != binding.TenantId || key.WalletId != payer.Context.WalletId || key.OperationId != binding.OperationId
                || key.Generation < 0 || string.IsNullOrWhiteSpace(key.StageRole) || key.SigningScope != payer.Context.Scope)
            || references is not null && references.Any(key => !expectedKeys.Contains(key)))
            return Error<WttStagePreparation>("WTT stage preparation requires the exact quote and participant.");
        var reservations = quote.Stages.Where(value => value.Stage == binding.Stage).ToArray();
        if (reservations.Length != 1) return Error<WttStagePreparation>("WTT stage is absent or duplicated in the immutable quote.");
        using var scope = _custodyScopeFactory?.CreateScope();
        var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
            ?? throw new InvalidOperationException("Durable WTT preparation storage is required.");
        var saved = await store.GetAsync(id, ct);
        if (saved is not null && saved.BindingJson != bindingJson)
            return Error<WttStagePreparation>("WTT generation already belongs to another immutable request.");
        if (saved?.Sealed == true) return Error<WttStagePreparation>("WTT generation is sealed against further signing or submission.");
        if (saved?.SignedPreparationJson is not null)
            return Ok(await ReadWttSolanaWireAsync(saved, binding, reservations[0], expectedKeys, compile, ct));
        if (quote.ExpiresAt <= DateTimeOffset.UtcNow) return Error<WttStagePreparation>("WTT quote expired before signature creation.");
        await checkCurrentState(ct);
        var vault = _wttStageKeyVault ?? scope?.ServiceProvider.GetRequiredService<WttStageKeyVault>()
            ?? throw new InvalidOperationException("Protected WTT stage-key custody is required.");
        if (saved is null)
        {
            var keys = new Dictionary<string, WttStageKeyBinding>(StringComparer.Ordinal);
            foreach (var keyBinding in expectedKeys)
            {
                var claimed = await vault.ClaimAsync(keyBinding, payer.Context, WttSolanaStageSignature.PublicKey, ct);
                if (claimed.IsError || claimed.Result is null) return Error<WttStagePreparation>(claimed.Message);
                if (claimed.Result.PublicKey == payer.Address) return Error<WttStagePreparation>("WTT stage-key role collides with the actual payer.");
                keys.Add(claimed.Result.PublicKey, keyBinding);
            }
            var recovery = await NewPlanAsync("", 0, 0, false, binding.IntentHash, ct);
            if ("solana:" + recovery.Genesis != binding.ProtocolIdentity)
                return Error<WttStagePreparation>("WTT quote is bound to another Solana genesis.");
            var message = compile(recovery, keys);
            var plan = new WttSolanaWirePlan(recovery, Convert.ToBase64String(message), keys);
            saved = await store.ClaimAsync(new WttPreparation
            {
                Id = id, BindingJson = bindingJson, PlanJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
        }
        if (saved.Sealed) return Error<WttStagePreparation>("WTT generation was sealed during preparation.");
        if (saved.SignedPreparationJson is not null)
            return Ok(await ReadWttSolanaWireAsync(saved, binding, reservations[0], expectedKeys, compile, ct));
        var winner = ReadWttSolanaPlan(saved, binding, expectedKeys, compile);
        if (await VerifyGenesisAsync(ct) != winner.Recovery.Genesis)
            return Error<WttStagePreparation>("Saved WTT preparation belongs to another Solana genesis.");
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.GetInt64() > winner.Recovery.LastValidBlockHeight)
            return Error<WttStagePreparation>("Unsigned WTT preparation expired; its generation must be retired by the operation journal.");
        var expectedMessage = Convert.FromBase64String(winner.MessageBase64);
        if (quote.ExpiresAt <= DateTimeOffset.UtcNow) return Error<WttStagePreparation>("WTT quote expired before signature creation.");
        if (await NativeMessageFeeAsync(expectedMessage, ct) > UInt64Text.Parse(reservations[0].NetworkFee))
            return Error<WttStagePreparation>("Current network fee exceeds the original WTT stage reservation.");
        if (verifyMessage is not null) await verifyMessage(expectedMessage, ct);
        if (quote.ExpiresAt <= DateTimeOffset.UtcNow) return Error<WttStagePreparation>("WTT quote expired before signature creation.");
        var required = SolanaMessageEnvelope.Read(expectedMessage).RequiredKeys.Select(key => key.Key).ToHashSet(StringComparer.Ordinal);
        if (winner.StageKeys.Any(key => references?.Contains(key.Value) == true && required.Contains(key.Key)))
            return Error<WttStagePreparation>("A public proof reference cannot acquire signing authority in the compiled stage.");
        var signers = winner.StageKeys.Where(key => references?.Contains(key.Value) != true).ToDictionary(key => key.Key, key => key.Value, StringComparer.Ordinal);
        var signed = await SignWttMessageAsync(expectedMessage, payer, signers, vault, ct);
        if (signed.IsError || signed.Result is null) return Error<WttStagePreparation>(signed.Message);
        var preparation = new WttStagePreparation(binding.Stage, binding.Generation,
            SolanaMessageEnvelope.VerifyWire(signed.Result, expectedMessage), Convert.ToBase64String(signed.Result),
            binding.ProtocolIdentity, saved.PlanJson, reservations[0]);
        var committed = await store.TrySaveSignedAsync(saved, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(preparation)), ct);
        if (committed is null)
        {
            committed = await store.GetAsync(id, ct);
            if (committed is null || committed.Sealed || committed.SignedPreparationJson is null)
                return Error<WttStagePreparation>("WTT generation changed or was cancelled before signature persistence.");
        }
        return Ok(await ReadWttSolanaWireAsync(committed, binding, reservations[0], expectedKeys, compile, ct));
    }

    private static WttSolanaWirePlan ReadWttSolanaPlan(WttPreparation saved, WttPreparationBinding binding,
        IReadOnlyList<WttStageKeyBinding> expectedKeys,
        Func<DurableSolanaPlan, IReadOnlyDictionary<string, WttStageKeyBinding>, byte[]> compile)
    {
        if (saved.BindingJson != CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)) || saved.Sealed)
            throw new InvalidDataException("WTT preparation identity changed or was sealed.");
        var plan = JsonSerializer.Deserialize<WttSolanaWirePlan>(saved.PlanJson) ?? throw new InvalidDataException("WTT unsigned plan is missing.");
        if (plan.Recovery is null || plan.StageKeys is null || plan.Recovery.IntentHash != binding.IntentHash
            || "solana:" + plan.Recovery.Genesis != binding.ProtocolIdentity || plan.Recovery.ContextSlot < 0 || plan.Recovery.LastValidBlockHeight <= 0
            || !WttSolanaKeyRoles.Exact(plan.StageKeys.Values, expectedKeys)
            || Convert.ToBase64String(compile(plan.Recovery, plan.StageKeys)) != plan.MessageBase64)
            throw new InvalidDataException("WTT saved unsigned message differs from its exact stage intent.");
        return plan;
    }

    private async Task<WttStagePreparation> ReadWttSolanaWireAsync(WttPreparation saved, WttPreparationBinding binding,
        WttStageReservation reservation, IReadOnlyList<WttStageKeyBinding> expectedKeys,
        Func<DurableSolanaPlan, IReadOnlyDictionary<string, WttStageKeyBinding>, byte[]> compile, CancellationToken ct)
    {
        var plan = ReadWttSolanaPlan(saved, binding, expectedKeys, compile);
        var prepared = JsonSerializer.Deserialize<WttStagePreparation>(saved.SignedPreparationJson!)
            ?? throw new InvalidDataException("WTT signed preparation is missing.");
        if (prepared.Stage != binding.Stage || prepared.Generation != binding.Generation || prepared.ProtocolIdentity != binding.ProtocolIdentity
            || prepared.RecoveryMetadata != saved.PlanJson || prepared.Reservation != reservation || prepared.AdditionalSignedTransactionsJson is not null
            || SolanaMessageEnvelope.VerifyWire(Convert.FromBase64String(prepared.SignedTransactionBase64), Convert.FromBase64String(plan.MessageBase64)) != prepared.TransactionId
            || await VerifyGenesisAsync(ct) != plan.Recovery.Genesis)
            throw new InvalidDataException("WTT saved signed candidate differs from its immutable preparation.");
        return prepared;
    }
}
