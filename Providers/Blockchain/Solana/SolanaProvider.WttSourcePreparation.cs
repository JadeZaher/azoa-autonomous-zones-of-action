using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Wormhole;
using Solnet.Wallet;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IWttSourceModule.PrepareWttSourceAsync"/>
    public async Task<AZOAResult<WttStagePreparation>> PrepareWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
        string stage, int generation, CancellationToken ct = default)
    {
        try
        {
            var plan = ReadWttSourcePlan(intent, quote);
            var step = plan.Steps.SingleOrDefault(value => value.Stage == stage && value.Stage is "Attest" or "Transfer")
                ?? throw new InvalidDataException("WTT publication stage is absent from the exact admitted quote.");
            if (generation < 0) throw new InvalidDataException("WTT generation cannot be negative.");
            var binding = WttSourceBinding(intent, quote, stage, generation);
            byte[] Compile(DurableSolanaPlan recovery, IReadOnlyDictionary<string, WttStageKeyBinding> keys)
                => CompileWttSource(intent, plan, step, recovery, WttSourceRole(keys, stage, "payer"), WttSourceRole(keys, stage, "message"));
            async Task Check(CancellationToken token)
            {
                var deployment = ReadWttSolanaDeployment() ?? throw new InvalidDataException("Pinned WTT deployment is unavailable.");
                var snapshot = await ReadWttSolanaSnapshotAsync(deployment, [], token);
                if (WttDescriptor(snapshot) != intent.Source || snapshot.MessageFee != plan.ProtocolFee
                    || await ReadWttSolanaGuardAsync(token) != plan.Guard)
                    throw new InvalidDataException("WTT protocol code, immutable guard, fee or rent changed before signing.");
                var inspected = await InspectWttSourceAsync(intent.Asset.TokenAddress, intent.Asset.NativeAsset, token);
                if (inspected.IsError || inspected.Result.Asset != intent.Asset || inspected.Result.Network != intent.Source)
                    throw new InvalidDataException("WTT source asset changed before signing.");
            }
            async Task Verify(byte[] message, CancellationToken token)
            {
                var tables = WttUsedTables(message, plan.LookupTables);
                await RequireCurrentLookupResolutionAsync(message, tables, token);
                var measured = await MeasureSolanaComputeAsync(message, tables.Count == 0 ? 0 : checked((long)tables.Max(value => value.Slot)), token);
                if (measured > step.ComputeUnits) throw new InvalidDataException("Current exact WTT message exceeds its frozen compute reservation.");
            }
            return await PrepareWttSolanaWireAsync(binding, quote, intent.SourceAuthority,
                WttSolanaKeyRoles.Source(binding, intent.SourceAuthority), Compile, Check, ct, Verify);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        { return Error<WttStagePreparation>(exception.Message); }
    }

    /// <inheritdoc cref="IWttSourceModule.SubmitWttSourceAsync"/>
    public async Task SubmitWttSourceAsync(WttOperationIntent intent, WttStageQuote quote, WttStagePreparation prepared,
        CancellationToken ct = default)
    {
        var (_, plan, wirePlan) = await ReadWttSourcePreparationAsync(intent, quote, prepared, ct);
        if (await VerifyGenesisAsync(ct) != wirePlan.Recovery.Genesis) throw new InvalidDataException("WTT signed wire belongs to another genesis.");
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.GetInt64() > wirePlan.Recovery.LastValidBlockHeight) return;
        var message = Convert.FromBase64String(wirePlan.MessageBase64);
        await RequireCurrentLookupResolutionAsync(message, WttUsedTables(message, plan.LookupTables), ct);
        var result = await DurableRpcAsync("sendTransaction", [prepared.SignedTransactionBase64,
            new { encoding = "base64", skipPreflight = false, preflightCommitment = "finalized", maxRetries = 0 }], ct);
        if (result.ValueKind != JsonValueKind.String || result.GetString() != prepared.TransactionId)
            throw new InvalidDataException("Solana send response did not identify the exact saved WTT transaction; its outcome requires observation.");
    }

    private async Task<(WttPreparation Saved, WttSolanaSourcePlan Plan, WttSolanaWirePlan Wire)> ReadWttSourcePreparationAsync(
        WttOperationIntent intent, WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct)
    {
        var plan = ReadWttSourcePlan(intent, quote);
        var step = plan.Steps.SingleOrDefault(value => value.Stage == prepared.Stage && value.Stage is "Attest" or "Transfer")
            ?? throw new InvalidDataException("WTT publication stage is absent from the saved quote.");
        var binding = WttSourceBinding(intent, quote, prepared.Stage, prepared.Generation);
        using var scope = _custodyScopeFactory?.CreateScope();
        var store = _wttPreparationStore ?? scope?.ServiceProvider.GetRequiredService<IWttPreparationStore>()
            ?? throw new InvalidOperationException("Durable WTT preparation storage is required.");
        var saved = await store.GetAsync(WttPreparationIdentity.Id(binding), ct) ?? throw new InvalidDataException("WTT preparation is not durably admitted.");
        byte[] Compile(DurableSolanaPlan recovery, IReadOnlyDictionary<string, WttStageKeyBinding> keys)
            => CompileWttSource(intent, plan, step, recovery, WttSourceRole(keys, prepared.Stage, "payer"), WttSourceRole(keys, prepared.Stage, "message"));
        var expectedKeys = WttSolanaKeyRoles.Source(binding, intent.SourceAuthority);
        var exact = await ReadWttSolanaWireAsync(saved, binding, step.Reservation, expectedKeys, Compile, ct);
        if (exact != prepared) throw new InvalidDataException("Supplied WTT transaction differs from the durable winning candidate.");
        return (saved, plan, ReadWttSolanaPlan(saved, binding, expectedKeys, Compile));
    }

    private WttSolanaSourcePlan ReadWttSourcePlan(WttOperationIntent intent, WttStageQuote quote)
    {
        if (!ValidWttSourceIntent(intent) || quote.Phase != "Source" || quote.ProtocolIdentity != intent.Source.ProtocolIdentity
            || quote.IntentHash != CanonicalJson.Hash(intent)) throw new InvalidDataException("WTT source quote differs from the exact frozen intent.");
        var plan = JsonSerializer.Deserialize<WttSolanaSourcePlan>(quote.PlanJson) ?? throw new InvalidDataException("WTT source plan is missing.");
        if (plan.Version != 1 || plan.IntentHash != quote.IntentHash || plan.Sender != intent.SourceAuthority.Address
            || plan.Core != intent.Source.CoreBridge || plan.TokenBridge != intent.Source.TokenBridge || plan.Guard is null
            || plan.Steps is null || plan.LookupTables is null || !plan.Steps.Select(value => value.Reservation).SequenceEqual(quote.Stages)
            || plan.Steps.Select(value => value.Stage).Distinct(StringComparer.Ordinal).Count() != plan.Steps.Count
            || CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(plan)) != quote.PlanJson
            || CanonicalJson.Hash(new { Version = 1, IntentHash = quote.IntentHash, Plan = quote.PlanJson, ExpiresAt = quote.ExpiresAt }) != quote.QuoteId)
            throw new InvalidDataException("WTT source plan or reservations differ from the immutable server quote.");
        var expectedStages = intent.RequiresAttestation
            ? new[] { "Attest", "Cleanup:Attest", "Transfer", "Cleanup:Transfer" }
            : new[] { "Transfer", "Cleanup:Transfer" };
        if (!plan.Steps.Select(value => value.Stage).SequenceEqual(expectedStages)
            || CanonicalJson.Hash(new { CoreHash = plan.CoreHash, TokenHash = plan.TokenHash }) != intent.Source.CodeIdentity)
            throw new InvalidDataException("WTT quote stage sequence or executable identities differ from the consented source deployment.");
        var feeBaseUnits = WttPayloadCodec.ParseAmount(intent.Amount.Fee) * BigInteger.Pow(10, Math.Max(0, intent.Asset.Decimals - 8));
        if (intent.ConsistencyLevel != 32 || intent.RequiresAttestation && intent.Asset.OriginChain != 1
            || feeBaseUnits > ulong.MaxValue || UInt64Text.Parse(intent.Amount.Debited) == 0
            || WttPayloadCodec.Normalize(intent.Amount.Requested, feeBaseUnits.ToString(System.Globalization.CultureInfo.InvariantCulture), intent.Asset.Decimals) != intent.Amount)
            throw new InvalidDataException("Saved source economics differ from exact protocol normalization or finalized publication policy.");
        var rent = Convert.FromBase64String(plan.Guard.RentBase64);
        foreach (var step in plan.Steps)
        {
            if (step.Reservation.ParticipantAddress != plan.Sender || step.Reservation.Stage != step.Stage)
                throw new InvalidDataException("WTT quote changed the payer or stage reservation identity.");
            if (step.Stage.StartsWith("Cleanup:", StringComparison.Ordinal))
            {
                if (step.Funding != 0 || step.CreateSourceAta || step.ComputeUnits != 0 || step.ComputePrice != 0
                    || step.Reservation.LockedDeposit != "0" || step.Reservation.Value != "0")
                    throw new InvalidDataException("Cleanup reservation cannot silently fund another publication.");
                continue;
            }
            var basicRent = checked(WttSolanaCostGuard.MinimumBalance(rent, 0) + WttSolanaCostGuard.MinimumBalance(rent, step.Stage == "Attest" ? 195 : 228));
            var maximumRent = checked(basicRent + WttSolanaCostGuard.MinimumBalance(rent, 8)
                + (step.Stage == "Transfer" && intent.Asset.OriginChain == 1 ? WttSolanaCostGuard.MinimumBalance(rent, 165) : 0));
            var ataRent = step.CreateSourceAta ? WttSolanaCostGuard.MinimumBalance(rent, 165) : 0;
            var expectedValue = checked(plan.ProtocolFee + (step.Stage == "Transfer" && intent.Asset.NativeAsset ? UInt64Text.Parse(intent.Amount.Debited) : 0));
            if (step.ComputeUnits == 0 || step.CreateSourceAta && (step.Stage != "Transfer" || !intent.Asset.NativeAsset)
                || step.Funding < checked(plan.ProtocolFee + basicRent) || step.Funding > checked(plan.ProtocolFee + maximumRent)
                || UInt64Text.Parse(step.Reservation.LockedDeposit) != checked(step.Funding - plan.ProtocolFee + ataRent)
                || UInt64Text.Parse(step.Reservation.Value) != expectedValue)
                throw new InvalidDataException("WTT funding or rent/value categories differ from the exact supported instruction plan.");
        }
        var budget = intent.SourceBudget;
        if (plan.Steps.Aggregate(BigInteger.Zero, (sum, step) => sum + WttPayloadCodec.ParseAmount(step.Reservation.NetworkFee)) > WttPayloadCodec.ParseAmount(budget.MaximumNetworkFee)
            || plan.Steps.Aggregate(BigInteger.Zero, (sum, step) => sum + WttPayloadCodec.ParseAmount(step.Reservation.LockedDeposit)) > WttPayloadCodec.ParseAmount(budget.MaximumLockedDeposit)
            || plan.Steps.Aggregate(BigInteger.Zero, (sum, step) => sum + WttPayloadCodec.ParseAmount(step.Reservation.Value)) > WttPayloadCodec.ParseAmount(budget.MaximumValue))
            throw new InvalidDataException("Saved WTT quote exceeds the original caller's immutable cost ceilings.");
        return plan;
    }

    private static WttPreparationBinding WttSourceBinding(WttOperationIntent intent, WttStageQuote quote, string stage, int generation)
        => new(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId, intent.OperationId, "Source", stage, generation, quote.IntentHash, quote.QuoteId);

    private static PublicKey WttSourceRole(IReadOnlyDictionary<string, WttStageKeyBinding> keys, string stage, string role)
    {
        var matching = keys.Where(value => value.Value.StageRole == "Source:" + stage + ":" + role).ToArray();
        if (keys.Count != 2 || matching.Length != 1) throw new InvalidDataException("WTT publication requires exactly its protected payer and message roles.");
        return new PublicKey(matching[0].Key);
    }

    private static IReadOnlyList<SolanaLookupTableSnapshot> WttUsedTables(byte[] message, IReadOnlyList<SolanaLookupTableSnapshot> saved)
        => UsedSolanaLookupTables(message, saved);
}
