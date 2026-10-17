using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Solnet.Programs;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider : INativeOperationModule
{
    private static readonly JsonSerializerOptions NativeSolanaJson = new(JsonSerializerDefaults.Web);
    private sealed record NativeSolanaInstruction(string Program, IReadOnlyList<SolanaInstructionAccount> Accounts, string Data);
    private sealed record NativeSolanaStage(string Name, IReadOnlyList<NativeSolanaInstruction> Instructions, string Fee, string Rent,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ProtocolLevy = null);
    private sealed record NativeSolanaQuote(string Resource, IReadOnlyList<NativeSolanaStage> Stages, string? ExpectedProgramHash = null,
        NativeOperationDisclosure? Disclosure = null, NativeSolanaStage? FallbackCleanup = null);
    private sealed record NativeSolanaPreparation(DurableSolanaPlan Recovery, string MessageBase64);

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeOperationQuote>> QuoteNativeOperationAsync(NativeOperationIntent intent, CancellationToken ct = default)
    {
        try { return await QuoteSolanaNativeCoreAsync(intent, ct); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or InvalidDataException)
        { return Error<NativeOperationQuote>(exception.Message); }
    }

    private async Task<AZOAResult<NativeOperationQuote>> QuoteSolanaNativeCoreAsync(NativeOperationIntent intent, CancellationToken ct)
    {
        if (!TrySolanaNativeIntent(intent, out var input)) return Error<NativeOperationQuote>("Invalid Solana native authority or typed operation.");
        var genesis = await VerifyGenesisAsync(ct);
        var specification = await PlanSolanaNativeAsync(intent, input, ct);
        var plan = await NewPlanAsync("", 0, 0, false, new string('0', 64), ct);
        var stages = new List<NativeSolanaStage>();
        foreach (var stage in specification.Stages)
        {
            var message = CompileNativeStage(plan, input.Payer, stage, new string('0', 64));
            var fee = await NativeMessageFeeAsync(message, ct);
            stages.Add(stage with { Fee = fee.ToString(CultureInfo.InvariantCulture) });
        }
        var cleanup = specification.FallbackCleanup;
        if (cleanup is not null)
        {
            var message = CompileNativeStage(plan, input.Payer, cleanup, new string('0', 64));
            cleanup = cleanup with { Fee = (await NativeMessageFeeAsync(message, ct)).ToString(CultureInfo.InvariantCulture) };
        }
        var quoted = specification with { Stages = stages, FallbackCleanup = cleanup };
        return Ok(new NativeOperationQuote("", "", "solana:" + genesis,
            (stages.Sum(stage => decimal.Parse(stage.Fee, CultureInfo.InvariantCulture))
                + (cleanup is null ? 0 : decimal.Parse(cleanup.Fee, CultureInfo.InvariantCulture))).ToString(CultureInfo.InvariantCulture),
            stages.Sum(stage => decimal.Parse(stage.Rent, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
            stages.Sum(stage => decimal.Parse(stage.ProtocolLevy ?? "0", CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(quoted), DateTimeOffset.UtcNow.AddMinutes(10), quoted.Disclosure));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeNextStage>> GetNextStageAsync(NativeOperationIntent intent, IReadOnlyList<NativeStageHistory> history, CancellationToken ct = default)
    {
        if (!TrySolanaNativeIntent(intent, out _) || !NativeOperationAuthorization.ValidQuote(intent, false))
            return Error<NativeNextStage>("Invalid quoted Solana operation.");
        var quote = JsonSerializer.Deserialize<NativeSolanaQuote>(intent.Quote!.PlanJson)!;
        var confirmed = history.Where(item => item.Observation.Verdict == "Confirmed").ToArray();
        if (history.Any(item => item.Observation.Verdict is not ("Confirmed" or "ExpiredNoEffect")) || confirmed.Length > quote.Stages.Count)
            return Error<NativeNextStage>("Solana native stage history is inconsistent.");
        for (var index = 0; index < confirmed.Length; index++)
        {
            if (confirmed[index].Prepared.Stage != quote.Stages[index].Name)
                return Error<NativeNextStage>("Solana native stage order differs from the immutable plan.");
            var proof = await ObserveNativeStageAsync(intent, confirmed[index].Prepared, ct);
            if (proof.IsError || proof.Result?.Verdict != "Confirmed")
                return Error<NativeNextStage>("Exact finalized Solana stage evidence is unavailable.");
        }
        return confirmed.Length < quote.Stages.Count
            ? Ok(new NativeNextStage(quote.Stages[confirmed.Length].Name, false,
                Reservation: new(quote.Stages[confirmed.Length].Fee, quote.Stages[confirmed.Length].Rent, quote.Stages[confirmed.Length].ProtocolLevy ?? "0")))
            : Ok(new NativeNextStage(null, true, quote.Resource, JsonSerializer.Serialize(new { resource = quote.Resource,
                transactionIds = confirmed.Select(item => item.Prepared.TransactionId).ToArray(), artifactHash = intent.ArtifactHash })));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeStagePreparation>> PrepareNativeStageAsync(NativeOperationIntent intent, string stage, int generation, CancellationToken ct = default)
    {
        try { return await PrepareSolanaNativeCoreAsync(intent, stage, generation, ct); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or InvalidDataException)
        { return Error<NativeStagePreparation>(exception.Message); }
    }

    private async Task<AZOAResult<NativeStagePreparation>> PrepareSolanaNativeCoreAsync(NativeOperationIntent intent, string stage, int generation, CancellationToken ct)
    {
        if (generation < 0 || !TrySolanaNativeIntent(intent, out var input) || !NativeOperationAuthorization.ValidQuote(intent, false))
            return Error<NativeStagePreparation>("Invalid Solana native preparation authority.");
        var specification = JsonSerializer.Deserialize<NativeSolanaQuote>(intent.Quote!.PlanJson)!;
        var selected = specification.Stages.SingleOrDefault(item => item.Name == stage)
            ?? (specification.FallbackCleanup?.Name == stage ? specification.FallbackCleanup : null);
        if (selected is null) return Error<NativeStagePreparation>("Stage is absent from the immutable Solana plan.");
        var identity = NativePreparationJournal.Identity(intent, stage, generation);
        var hash = CanonicalJson.Hash(intent);
        return await NativePreparationJournal.UseAsync(_custodyScopeFactory, async store =>
        {
            var row = await store.GetAsync(identity, ct);
            if (row is null)
            {
                await ValidateSolanaNativeStageStateAsync(input, specification, selected, ct);
                var recovery = await NewPlanAsync("", 0, 0, false, hash, ct);
                if (intent.ProtocolIdentity != "solana:" + recovery.Genesis) return Error<NativeStagePreparation>("Solana genesis changed.");
                var message = CompileNativeStage(recovery, input.Payer, selected, hash);
                if (await NativeMessageFeeAsync(message, ct) > ulong.Parse(selected.Fee, CultureInfo.InvariantCulture))
                    return Error<NativeStagePreparation>("Current Solana stage fees exceed the admitted quote.");
                row = await store.ClaimAsync(new NativePreparation { Id = identity, IntentHash = hash, Stage = stage, Generation = generation,
                    CreatedAt = DateTimeOffset.UtcNow, PlanJson = JsonSerializer.Serialize(new NativeSolanaPreparation(recovery, Convert.ToBase64String(message))) }, ct);
            }
            if (row.IntentHash != hash || row.Stage != stage || row.Generation != generation)
                return Error<NativeStagePreparation>("Solana preparation key belongs to another intent.");
            if (row.PreparedJson is not null) return Ok(JsonSerializer.Deserialize<NativeStagePreparation>(row.PreparedJson)!);
            var saved = JsonSerializer.Deserialize<NativeSolanaPreparation>(row.PlanJson)!;
            var expected = CompileNativeStage(saved.Recovery, input.Payer, selected, hash);
            if (saved.MessageBase64 != Convert.ToBase64String(expected)) throw new InvalidOperationException("Saved Solana preparation semantics changed.");
            var required = Message.Deserialize(expected).AccountKeys.Take(Message.Deserialize(expected).Header.RequiredSignatures).Select(key => key.Key).ToHashSet(StringComparer.Ordinal);
            var contexts = new Dictionary<string, SigningContext>(StringComparer.Ordinal);
            foreach (var authority in intent.Authorities.Where(authority => required.Contains(authority.Address)))
            {
                if (contexts.TryGetValue(authority.Address, out var prior) && prior != authority.SigningContext)
                    return Error<NativeStagePreparation>("Conflicting custody authorities share a Solana public key.");
                contexts[authority.Address] = authority.SigningContext;
            }
            var signed = await SignDurableAsync(expected, contexts, ct);
            if (signed.IsError || signed.Result is null) return Error<NativeStagePreparation>(signed.Message);
            var wire = Convert.ToBase64String(signed.Result);
            var transactionId = Encoders.Base58.EncodeData(Transaction.Deserialize(signed.Result).Signatures[0].Signature);
            if (!MatchesDurableWire(wire, transactionId, expected, out _)) throw new InvalidOperationException("Solana custody returned a noncanonical wire.");
            var prepared = new NativeStagePreparation(stage, transactionId, wire, intent.ProtocolIdentity,
                JsonSerializer.Serialize(new { preparationId = identity }), selected.Fee, selected.Rent, ReservedValue: selected.ProtocolLevy ?? "0");
            var persisted = await store.SavePreparedAsync(identity, hash, JsonSerializer.Serialize(prepared), ct);
            return Ok(JsonSerializer.Deserialize<NativeStagePreparation>(persisted.PreparedJson!)!);
        });
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<string>> SubmitNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default)
    {
        var saved = await ValidateNativeSolanaPreparedAsync(intent, prepared, ct);
        await SubmitDurableAsync(prepared.SignedTransactionBase64, prepared.TransactionId, saved.Recovery, ct);
        return Ok(prepared.TransactionId);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<NativeStageObservation>> ObserveNativeStageAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct = default)
    {
        var saved = await ValidateNativeSolanaPreparedAsync(intent, prepared, ct);
        var status = await ObserveDurableAsync(prepared.TransactionId, prepared.SignedTransactionBase64, saved.Recovery, ct);
        var quote = JsonSerializer.Deserialize<NativeSolanaQuote>(intent.Quote!.PlanJson)!;
        if (status == "Confirmed" && prepared.Stage is "Call" or "Deploy" or "Upgrade"
            && (quote.ExpectedProgramHash is null || !await ProveNativeProgramAtCallAsync(quote.Resource, quote.ExpectedProgramHash,
                prepared.TransactionId, ct, includeTransaction: prepared.Stage != "Call"))) status = "NeedsOperator";
        var accounting = status is "Confirmed" or "FailedOnChain" ? await ReadNativeAccountingAsync(prepared.TransactionId, ct) : null;
        var resource = quote.Resource;
        if (intent.Kind != "Recovery" && prepared.Stage is "CreateBuffer" or "CloseBuffer")
        {
            var input = intent.Input.Deserialize<SolanaNativeInput>(NativeSolanaJson)!;
            resource = SolanaNativeLoader.Seeded(new PublicKey(input.Authority), SolanaNativeLoader.Seed(intent.OperationKey, "buffer")).Key;
        }
        if (status == "Confirmed" && prepared.Stage == "CloseBuffer")
        {
            if (!TrySolanaNativeIntent(intent, out var input) || accounting is null
                || !ProvesBufferRefund(quote, input, accounting, resource)) status = "NeedsOperator";
        }
        var effectKind = prepared.Stage switch
        {
            "CreateBuffer" => "BufferFunded", "Deploy" => "Deployed", "Upgrade" => "Upgraded",
            "RemoveAuthority" => "AuthorityFinalized", "CloseBuffer" => "BufferClosed", _ => null,
        };
        var effect = status == "Confirmed" && effectKind is not null
            ? new NativeResourceEffect(effectKind, resource, prepared.TransactionId,
                JsonSerializer.Serialize(new { stage = prepared.Stage, intentHash = CanonicalJson.Hash(intent) })) : null;
        return Ok(new NativeStageObservation(status switch { "Confirmed" => "Confirmed", "FailedOnChain" => "FailedOnChain", "Unseen" => "Pending", _ => "Unknown" },
            prepared.TransactionId, prepared.ProtocolIdentity, status == "Confirmed" ? resource : null,
            status == "Confirmed" ? JsonSerializer.Serialize(new { transactionId = prepared.TransactionId, stage = prepared.Stage, intentHash = CanonicalJson.Hash(intent) }) : null,
            status == "NeedsOperator" ? "Retain the original signature until finalized wire history is available." : null, accounting,
            prepared.Stage == "CloseBuffer" ? null : effect, prepared.Stage == "CloseBuffer" ? effect : null));
    }

    private async Task<NativeStageAccounting?> ReadNativeAccountingAsync(string signature, CancellationToken ct)
    {
        var transaction = await DurableRpcAsync("getTransaction", [signature, new { encoding = "json", commitment = "finalized", maxSupportedTransactionVersion = 0 }], ct);
        if (transaction.ValueKind == JsonValueKind.Null) return null;
        var meta = transaction.GetProperty("meta");
        var keys = transaction.GetProperty("transaction").GetProperty("message").GetProperty("accountKeys").EnumerateArray().Select(key => key.GetString()!).ToList();
        if (meta.TryGetProperty("loadedAddresses", out var loaded))
        {
            keys.AddRange(loaded.GetProperty("writable").EnumerateArray().Select(key => key.GetString()!));
            keys.AddRange(loaded.GetProperty("readonly").EnumerateArray().Select(key => key.GetString()!));
        }
        var before = meta.GetProperty("preBalances"); var after = meta.GetProperty("postBalances");
        if (before.GetArrayLength() != keys.Count || after.GetArrayLength() != keys.Count) return null;
        var changes = new List<NativeBalanceChange>();
        for (var index = 0; index < keys.Count; index++)
            if (before[index].GetUInt64() != after[index].GetUInt64()) changes.Add(new(keys[index],
                before[index].GetUInt64().ToString(CultureInfo.InvariantCulture), after[index].GetUInt64().ToString(CultureInfo.InvariantCulture)));
        return new(meta.GetProperty("fee").GetUInt64().ToString(CultureInfo.InvariantCulture), changes);
    }

    private async Task<NativeSolanaPreparation> ValidateNativeSolanaPreparedAsync(NativeOperationIntent intent, NativeStagePreparation prepared, CancellationToken ct)
    {
        if (!TrySolanaNativeIntent(intent, out var input) || !NativeOperationAuthorization.ValidQuote(intent, false)
            || prepared.ProtocolIdentity != intent.ProtocolIdentity || prepared.AdditionalSignedTransactionsJson is not null)
            throw new InvalidOperationException("Invalid Solana native prepared authorization.");
        using var recovery = JsonDocument.Parse(prepared.RecoveryMetadata);
        var identity = recovery.RootElement.GetProperty("preparationId").GetString()!;
        var row = await NativePreparationJournal.UseAsync(_custodyScopeFactory, store => store.GetAsync(identity, ct));
        if (row is null || row.IntentHash != CanonicalJson.Hash(intent) || row.PreparedJson != JsonSerializer.Serialize(prepared)
            || row.Stage != prepared.Stage || identity != NativePreparationJournal.Identity(intent, row.Stage, row.Generation))
            throw new InvalidOperationException("Solana wire is not its durable admitted candidate.");
        var saved = JsonSerializer.Deserialize<NativeSolanaPreparation>(row.PlanJson)!;
        var quote = JsonSerializer.Deserialize<NativeSolanaQuote>(intent.Quote!.PlanJson)!;
        var stage = quote.Stages.SingleOrDefault(item => item.Name == prepared.Stage)
            ?? (quote.FallbackCleanup?.Name == prepared.Stage ? quote.FallbackCleanup : null)
            ?? throw new InvalidOperationException("Prepared stage is absent from the original quote.");
        var message = CompileNativeStage(saved.Recovery, input.Payer, stage, row.IntentHash);
        if (prepared.ReservedFee != stage.Fee || prepared.ReservedRent != stage.Rent || prepared.ReservedValue != (stage.ProtocolLevy ?? "0")
            || saved.MessageBase64 != Convert.ToBase64String(message)
            || !MatchesDurableWire(prepared.SignedTransactionBase64, prepared.TransactionId, message, out _))
            throw new InvalidOperationException("Solana canonical wire or economic authorization changed.");
        return saved;
    }

    private static byte[] CompileNativeStage(DurableSolanaPlan recovery, string payer, NativeSolanaStage stage, string hash)
        => CompileDurable(recovery, new PublicKey(payer), stage.Instructions.Select(DecodeNativeInstruction)
            .Append(MemoProgram.NewMemoV2("azoa:native:" + hash, new PublicKey(payer))).ToArray());

    private async Task<ulong> NativeMessageFeeAsync(byte[] message, CancellationToken ct)
    {
        var parsed = SolanaMessageEnvelope.Read(message);
        if (!SolanaPacketBudget.Fits(message.Length, parsed.RequiredSignatures)) throw new ArgumentException("Solana native stage exceeds packet size.");
        var fee = await DurableRpcAsync("getFeeForMessage", [Convert.ToBase64String(message), new { commitment = "finalized" }], ct);
        if (!fee.GetProperty("value").TryGetUInt64(out var lamports)) throw new InvalidOperationException("Solana message fee unavailable.");
        return lamports;
    }

    private static NativeSolanaInstruction EncodeNativeInstruction(TransactionInstruction instruction)
        => new(new PublicKey(instruction.ProgramId).Key, instruction.Keys.Select(account => new SolanaInstructionAccount(account.PublicKey, account.IsWritable, account.IsSigner)).ToArray(), Convert.ToBase64String(instruction.Data));
    private static TransactionInstruction DecodeNativeInstruction(NativeSolanaInstruction instruction)
        => new() { ProgramId = new PublicKey(instruction.Program).KeyBytes, Data = Convert.FromBase64String(instruction.Data),
            Keys = instruction.Accounts.Select(account => account.Writable ? AccountMeta.Writable(new PublicKey(account.Address), account.Signer)
                : AccountMeta.ReadOnly(new PublicKey(account.Address), account.Signer)).ToList() };
}
