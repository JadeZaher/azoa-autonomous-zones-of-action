using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldCommandStore
{
    /// <inheritdoc/>
    public async Task<HoldCommand?> RecordNoEffectAsync(HoldCommand expected, HoldTransferEvidence evidence,
        CancellationToken ct = default)
    {
        if (expected.State != "Submitted" || expected.Version is < 0 or long.MaxValue || expected.PreparedJson is null
            || evidence.Version != 1 || evidence.Confirmation != ChainConfirmation.FailedOnChain
            || evidence.PreparedHash != CanonicalJson.HashCanonical(expected.PreparedJson)) return null;
        var wire = JsonSerializer.Deserialize<PreparedValueTransfer>(expected.PreparedJson)!;
        if (evidence.TransactionId != wire.TransactionId || evidence.ProtocolIdentity != wire.ProtocolIdentity) return null;
        var query = SurrealWriter.UpdateOnly<HoldCommand>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == "Submitted" && row.IntentHash == expected.IntentHash
                && row.PreparedJson == expected.PreparedJson && row.Generation == expected.Generation)
            .Set(row => row.State, "FailedNoEffect").Set(row => row.Version, expected.Version + 1)
            .Set(row => row.ObservationJson, CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(evidence)))
            .Set(row => row.Detail, "The original transfer is authoritatively proven to have no effect. Explicit replacement or cancellation is required.")
            .Set(row => row.UpdatedAt, evidence.ObservedAt).Build();
        var response = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var result = await executor.ExecuteAsync(query, ct);
            result.EnsureAllOk(); return result;
        }, ct);
        return response.GetValues<HoldCommand>(0).SingleOrDefault();
    }
}
