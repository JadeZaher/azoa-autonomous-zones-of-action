using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    /// <inheritdoc cref="IDexTransactionModule.SubmitSwapStageAsync"/>
    public async Task<AZOAResult<IReadOnlyList<string>>> SubmitSwapStageAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default)
    {
        var validated = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (validated.IsError || validated.Result is not { } exact ||
            !exact.TransactionIds.SequenceEqual(prepared.TransactionIds) || exact.SignedPayloadJson != prepared.SignedPayloadJson)
            return AZOAResult<IReadOnlyList<string>>.Failure("Saved Jupiter wire or transaction identity changed.");
        var (saved, compiled) = ReadJupiterPlan(intent, prepared.Plan);
        if (await VerifyGenesisAsync(ct) != saved.Recovery.Genesis)
            return AZOAResult<IReadOnlyList<string>>.Failure("Saved Jupiter wire belongs to another chain genesis.");
        var height = await DurableRpcAsync("getBlockHeight", [new { commitment = "finalized" }], ct);
        if (height.ValueKind != JsonValueKind.Number || !height.TryGetInt64(out var currentHeight) || currentHeight < 0)
            throw new JsonException("Finalized Solana block height is missing.");
        if (currentHeight > saved.Recovery.LastValidBlockHeight)
            return AZOAResult<IReadOnlyList<string>>.Success(exact.TransactionIds, "Validity elapsed; observe the original signature.");
        if (await NativeMessageFeeAsync(compiled.Message, ct) > UInt64Text.Parse(saved.NetworkFee))
            return AZOAResult<IReadOnlyList<string>>.Failure("Current Jupiter message fee exceeds the original reservation.");
        await RequireCurrentLookupResolutionAsync(compiled.Message, compiled.LookupTables, ct);
        var wire = JsonSerializer.Deserialize<string>(exact.SignedPayloadJson)!;
        await SubmitDurableAsync(wire, exact.TransactionIds.Single(), saved.Recovery, ct);
        return AZOAResult<IReadOnlyList<string>>.Success(exact.TransactionIds);
    }
}
