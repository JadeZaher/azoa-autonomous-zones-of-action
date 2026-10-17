using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc/>
    public async Task<AZOAResult<NativeOperationQuote>> QuoteNativeOperationAsync(NativeOperationIntent intent, CancellationToken ct = default)
    {
        if (!TryAvmIntent(intent, out var input, out var application)) return Error<NativeOperationQuote>("Invalid typed AVM operation.");
        if (input.Operation == "Call" && !await AvmApplicationMatchesAsync(input.ApplicationId!, application, ct))
            return Error<NativeOperationQuote>("Application programs do not match the selected artifact.");
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new InvalidOperationException("Algod transaction parameters unavailable.");
        var transaction = BuildAvmCall(input, application);
        ApplySuggestedParams(transaction, parameters);
        transaction.Note = new byte[64];
        transaction.Fee = AvmFee(transaction, parameters);
        transaction.Fee = AvmFee(transaction, parameters);
        return Ok(new NativeOperationQuote("", "", "algorand:" + parameters.GenesisHashB64,
            transaction.Fee!.Value.ToString(CultureInfo.InvariantCulture), AvmMinimumBalance(input, application).ToString(CultureInfo.InvariantCulture),
            "0", JsonSerializer.Serialize(new { format = "avm-native-plan/1", artifactHash = CanonicalJson.Hash(input.Artifact) }), DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeNextStage>> GetNextStageAsync(NativeOperationIntent intent, IReadOnlyList<NativeStageHistory> history, CancellationToken ct = default)
    {
        if (!TryAvmIntent(intent, out _, out _)) return Error<NativeNextStage>("Invalid AVM operation identity.");
        var completed = history.Where(item => item.Observation.Verdict == "Confirmed").ToArray();
        if (completed.Length == 0 && history.All(item => item.Observation.Verdict == "ExpiredNoEffect"))
            return Ok(new NativeNextStage("Execute", false));
        if (completed.Length != 1 || history.Any(item => item.Observation.Verdict is not ("Confirmed" or "ExpiredNoEffect")))
            return Error<NativeNextStage>("AVM stage history is inconsistent.");
        var observed = await ObserveNativeStageAsync(intent, completed[0].Prepared, ct);
        return observed.IsError || observed.Result is null || observed.Result.Verdict != "Confirmed"
            ? Error<NativeNextStage>("Authoritative AVM completion evidence is unavailable.")
            : Ok(new NativeNextStage(null, true, observed.Result.ResourceId, observed.Result.EvidenceJson));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeStagePreparation>> PrepareNativeStageAsync(NativeOperationIntent intent, string stage, int generation, CancellationToken ct = default)
    {
        if (stage != "Execute" || generation < 0 || !TryAvmIntent(intent, out var input, out var application)
            || !NativeOperationAuthorization.ValidQuote(intent, false)) return Error<NativeStagePreparation>("Invalid quoted AVM authorization.");
        var identity = NativePreparationJournal.Identity(intent, stage, generation);
        var hash = CanonicalJson.Hash(intent);
        return await NativePreparationJournal.UseAsync(_custodyScopeFactory, async store =>
        {
            var row = await store.GetAsync(identity, ct);
            if (row is null)
            {
                if (input.Operation == "Call" && !await AvmApplicationMatchesAsync(input.ApplicationId!, application, ct))
                    return Error<NativeStagePreparation>("Application program identity changed before signing.");
                var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
                    ?? throw new InvalidOperationException("Algod transaction parameters unavailable.");
                if (intent.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
                    return Error<NativeStagePreparation>("AVM authorization belongs to another genesis.");
                var transaction = BuildAvmCall(input, application);
                ApplySuggestedParams(transaction, parameters);
                transaction.Note = System.Text.Encoding.UTF8.GetBytes(hash);
                transaction.Fee = ulong.Parse(intent.Quote!.NetworkFee, CultureInfo.InvariantCulture);
                var rent = AvmMinimumBalance(input, application);
                if (transaction.Fee < AvmFee(transaction, parameters) || intent.Quote.LockedRent != rent.ToString(CultureInfo.InvariantCulture)
                    || intent.Quote.Value != "0") return Error<NativeStagePreparation>("Current AVM costs exceed the quoted fee or minimum balance.");
                row = await store.ClaimAsync(new NativePreparation
                {
                    Id = identity, IntentHash = hash, Stage = stage, Generation = generation, CreatedAt = DateTimeOffset.UtcNow,
                    PlanJson = JsonSerializer.Serialize(new AvmPreparedPlan(Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(transaction)),
                        intent.ProtocolIdentity, rent.ToString(CultureInfo.InvariantCulture), hash)),
                }, ct);
            }
            if (row.IntentHash != hash || row.Stage != stage || row.Generation != generation)
                return Error<NativeStagePreparation>("AVM preparation key belongs to another immutable intent.");
            if (row.PreparedJson is not null) return Ok(JsonSerializer.Deserialize<NativeStagePreparation>(row.PreparedJson)!);
            var plan = JsonSerializer.Deserialize<AvmPreparedPlan>(row.PlanJson) ?? throw new InvalidOperationException("AVM preparation plan missing.");
            var unsigned = Convert.FromBase64String(plan.UnsignedBase64);
            var signing = await SignViaCustodyAsync(unsigned, "prepare authorized AVM operation", intent.Authorities[0].SigningContext);
            if (signing.IsError || signing.Result is null) return Error<NativeStagePreparation>(signing.Message);
            var signed = AlgorandSignedEnvelope.DecodeCanonical(Convert.ToBase64String(signing.Result));
            if (!Encoder.EncodeToMsgPackOrdered(signed.Tx).AsSpan().SequenceEqual(unsigned))
                throw new InvalidOperationException("Custody returned different AVM transaction semantics.");
            var prepared = new NativeStagePreparation(stage, signed.Tx.TxID(), Convert.ToBase64String(signing.Result), plan.Genesis,
                JsonSerializer.Serialize(new { preparationId = identity, intentHash = hash }), signed.Tx.Fee!.Value.ToString(CultureInfo.InvariantCulture), plan.Rent);
            var saved = await store.SavePreparedAsync(identity, hash, JsonSerializer.Serialize(prepared), ct);
            return Ok(JsonSerializer.Deserialize<NativeStagePreparation>(saved.PreparedJson!)!);
        });
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> SubmitNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default)
    {
        var transaction = await ValidateAvmPreparedAsync(intent, prepared, ct);
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct);
        if (parameters is null || prepared.ProtocolIdentity != "algorand:" + parameters.GenesisHashB64)
            return Error<string>("Prepared AVM genesis differs from this node.");
        if ((ulong)parameters.LastRound > transaction.LastValid) return Ok(prepared.TransactionId);
        using var content = new ByteArrayContent(Convert.FromBase64String(prepared.SignedTransactionBase64));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
        using var submitted = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!submitted.RootElement.TryGetProperty("txId", out var id) || id.GetString() != prepared.TransactionId)
            throw new HttpRequestException("Algod returned a different native transaction identity.");
        return Ok(prepared.TransactionId);
    }

    private async Task<Transaction> ValidateAvmPreparedAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct)
    {
        if (!TryAvmIntent(intent, out _, out _) || !NativeOperationAuthorization.ValidQuote(intent, false)
            || prepared.Stage != "Execute" || prepared.ProtocolIdentity != intent.ProtocolIdentity
            || prepared.AdditionalSignedTransactionsJson is not null || prepared.ReservedValue != "0")
            throw new InvalidOperationException("Prepared AVM authorization is invalid.");
        using var recovery = JsonDocument.Parse(prepared.RecoveryMetadata);
        var identity = recovery.RootElement.GetProperty("preparationId").GetString()!;
        var hash = CanonicalJson.Hash(intent);
        var row = await NativePreparationJournal.UseAsync(_custodyScopeFactory, store => store.GetAsync(identity, ct));
        if (row is null || row.IntentHash != hash || row.Stage != prepared.Stage
            || identity != NativePreparationJournal.Identity(intent, row.Stage, row.Generation)
            || row.PreparedJson != JsonSerializer.Serialize(prepared)) throw new InvalidOperationException("AVM wire is not its durable signed candidate.");
        var signed = AlgorandSignedEnvelope.DecodeCanonical(prepared.SignedTransactionBase64);
        if (signed.Tx.TxID() != prepared.TransactionId) throw new InvalidOperationException("AVM transaction identity changed.");
        return signed.Tx;
    }
}
