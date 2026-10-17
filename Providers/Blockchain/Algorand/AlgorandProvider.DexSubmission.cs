using System.Net.Http.Headers;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IDexTransactionModule.SubmitSwapStageAsync"/>
    public async Task<AZOAResult<IReadOnlyList<string>>> SubmitSwapStageAsync(SwapIntent intent,
        PreparedSwapStage prepared, CancellationToken ct = default)
    {
        var validated = ValidateSignedSwapStage(intent, prepared.Plan, prepared.SignedPayloadJson);
        if (validated.IsError || validated.Result is not { } exact ||
            !exact.TransactionIds.SequenceEqual(prepared.TransactionIds) || exact.SignedPayloadJson != prepared.SignedPayloadJson)
            return AZOAResult<IReadOnlyList<string>>.Failure("Saved Tinyman wire or transaction identities changed.");
        var wire = ReadTinymanPlan(intent, prepared.Plan);
        var parameters = await AlgodHttpClient.GetFromJsonAsync<AlgodSuggestedParams>("/v2/transactions/params", ct)
            ?? throw new JsonException("Algod submission parameters are missing.");
        if (parameters.LastRound < 1 || parameters.GenesisHashB64 != wire.GenesisHash || (parameters.GenesisId ?? "") != wire.GenesisId)
            return AZOAResult<IReadOnlyList<string>>.Failure("Saved Tinyman wire belongs to another chain genesis.");
        if ((ulong)parameters.LastRound > wire.LastValid)
            return AZOAResult<IReadOnlyList<string>>.Success(exact.TransactionIds, "Validity window elapsed; authoritative observation is required.");
        using var envelopes = JsonDocument.Parse(exact.SignedPayloadJson);
        var bytes = envelopes.RootElement.EnumerateArray().SelectMany(item => Convert.FromBase64String(item.GetString()!)).ToArray();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
        using var submitted = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!submitted.RootElement.TryGetProperty("txId", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() != exact.TransactionIds[0])
            throw new HttpRequestException("Algod returned a different Tinyman group identity.");
        return AZOAResult<IReadOnlyList<string>>.Success(exact.TransactionIds);
    }
}
