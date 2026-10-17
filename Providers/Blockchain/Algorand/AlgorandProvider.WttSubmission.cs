using System.Net.Http.Headers;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Wtt;
using Microsoft.Extensions.DependencyInjection;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

public partial class AlgorandProvider
{
    /// <inheritdoc cref="IWttSourceModule.SubmitWttSourceAsync"/>
    public async Task SubmitWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default)
    {
        var (plan, wire) = await ReadWttAlgoSavedSourceAsync(intent, quote, prepared, ct);
        await SubmitWttAlgoGroupAsync(plan.GenesisHash, plan.GenesisId, wire.FirstValid, prepared, ct);
    }

    /// <inheritdoc cref="IWttTargetModule.SubmitWttTargetAsync"/>
    public async Task SubmitWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote,
        WttStagePreparation prepared, CancellationToken ct = default)
    {
        var (plan, wire) = await ReadWttAlgoSavedTargetAsync(intent, proofs, quote, prepared, ct);
        await SubmitWttAlgoGroupAsync(plan.GenesisHash, plan.GenesisId, wire.FirstValid, prepared, ct);
    }

    private async Task SubmitWttAlgoGroupAsync(string genesisHash, string genesisId, ulong firstValid,
        WttStagePreparation prepared, CancellationToken ct)
    {
        var parameters = await ReadWttAlgorandJsonAsync("/v2/transactions/params", ct);
        var round = WttAlgoUnsigned(parameters, "last-round");
        if (round == 0 || WttAlgoText(parameters, "genesis-hash") != genesisHash
            || WttAlgoText(parameters, "genesis-id") != genesisId)
            throw new InvalidDataException("Saved WTT group belongs to another physical network.");
        if (round > checked(firstValid + 1000)) return;
        if (round < firstValid) throw new InvalidDataException("Algorand has not reached the saved group's validity window.");
        var additional = JsonSerializer.Deserialize<string[]>(prepared.AdditionalSignedTransactionsJson!)!;
        var bytes = new[] { prepared.SignedTransactionBase64 }.Concat(additional).SelectMany(Convert.FromBase64String).ToArray();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-binary");
        using var response = await AlgodHttpClient.PostAsync("/v2/transactions", content, ct);
        response.EnsureSuccessStatusCode();
        using var submitted = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (WttAlgoText(submitted.RootElement, "txId") != prepared.TransactionId)
            throw new HttpRequestException("Algod returned a different WTT group identity; reconcile the saved group.");
    }

    private async Task<(WttAlgorandTargetPlan Plan, WttAlgoTargetWirePlan Wire)> ReadWttAlgoSavedTargetAsync(
        WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct)
    {
        var plan = ReadWttAlgoTargetQuote(intent, proofs, quote);
        var step = plan.Steps.SingleOrDefault(item => item.Stage == prepared.Stage)
            ?? throw new InvalidDataException("Saved target stage is absent from its admitted plan.");
        var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
            intent.OperationId, "Target", prepared.Stage, prepared.Generation, quote.IntentHash, quote.QuoteId);
        using var scope = _custodyScopeFactory?.CreateScope()
            ?? throw new InvalidOperationException("Durable WTT preparation storage is required.");
        var row = await scope.ServiceProvider.GetRequiredService<IWttPreparationStore>().GetAsync(WttPreparationIdentity.Id(binding), ct)
            ?? throw new InvalidDataException("Target preparation was never durably admitted.");
        if (row.SignedPreparationJson is null || ReadWttAlgoTargetPrepared(row, binding, intent, plan, step) != prepared)
            throw new InvalidDataException("Submission differs from the exact durable target signed candidate.");
        return (plan, ReadWttAlgoTargetWire(row, binding, intent, plan, step));
    }

    private async Task<(WttAlgorandSourcePlan Plan, WttAlgoWirePlan Wire)> ReadWttAlgoSavedSourceAsync(
        WttOperationIntent intent, WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct)
    {
        var plan = ReadWttAlgoSourceQuote(intent, quote);
        var step = plan.Steps.SingleOrDefault(item => item.Stage == prepared.Stage)
            ?? throw new InvalidDataException("Saved WTT stage is absent from its admitted plan.");
        var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId,
            intent.OperationId, "Source", prepared.Stage, prepared.Generation, quote.IntentHash, quote.QuoteId);
        using var scope = _custodyScopeFactory?.CreateScope()
            ?? throw new InvalidOperationException("Durable WTT preparation storage is required.");
        var row = await scope.ServiceProvider.GetRequiredService<IWttPreparationStore>().GetAsync(WttPreparationIdentity.Id(binding), ct)
            ?? throw new InvalidDataException("WTT preparation was never durably admitted.");
        if (row.SignedPreparationJson is null || ReadWttAlgoPrepared(row, binding, intent, plan, step) != prepared)
            throw new InvalidDataException("Submission does not match the exact durable WTT signed candidate.");
        return (plan, ReadWttAlgoWire(row, binding, intent, plan, step));
    }
}
