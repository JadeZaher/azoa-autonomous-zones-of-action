using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private async Task<ulong> MeasureSolanaComputeAsync(byte[] compiledMessage, long minimumSlot, CancellationToken ct)
    {
        var simulation = SolanaComputeBudget.SimulationWire(compiledMessage);
        var response = await DurableRpcAsync("simulateTransaction", [Convert.ToBase64String(simulation),
            new { encoding = "base64", sigVerify = false, replaceRecentBlockhash = false, commitment = "finalized", minContextSlot = minimumSlot }], ct);
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("context", out var context)
            || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("slot", out var slot)
            || slot.ValueKind != JsonValueKind.Number || !slot.TryGetInt64(out var observedSlot) || observedSlot < minimumSlot
            || !response.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("err", out var error) || error.ValueKind != JsonValueKind.Null
            || !value.TryGetProperty("unitsConsumed", out var units) || units.ValueKind != JsonValueKind.Number
            || !units.TryGetUInt64(out var consumed) || consumed == 0)
            throw new InvalidDataException("The exact transaction did not simulate successfully with measurable compute consumption.");
        return consumed;
    }
}
