using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealHoldReleaseMandateStore
{
    /// <inheritdoc/>
    public Task<HoldReleaseMandateOffer?> GetOfferAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<HoldReleaseMandateOffer>(SurrealQuery<HoldReleaseMandateOffer>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<HoldReleaseMandateOffer?> OfferAsync(HoldReleaseMandateRequest request, SigningConsentWitness authority,
        CancellationToken ct = default)
    {
        if (request?.Terms is null || request.RequestId == Guid.Empty) return null;
        var credential = authority.Credential;
        var id = HoldReleaseMandateIdentity.Id(credential.ActorUserId, credential.TenantId, request.RequestId);
        var inputHash = CanonicalJson.Hash(new HoldReleaseMandateOfferRequest(request.RequestId, request.HoldVersion, request.Terms));
        var existing = await GetOfferAsync(id, ct);
        if (existing is not null) return existing.InputHash == inputHash ? existing : null;
        var hold = await executor.QuerySingleAsync<HoldLedger>(SurrealQuery<HoldLedger>.Key(request.Terms.HoldRecordId), ct);
        if (hold is null || hold.OwnerUserId != credential.ActorUserId.ToString("N")
            || SurrealRecordGuid.ParseOptional(hold.TenantId) != credential.TenantId || !HoldReleaseMandateIdentity.ValidTerms(hold, request)) return null;
        var guards = SigningAuthorityFence.ForConsent(authority, [AzoaScopes.TransferSign], "hold_mandate_offer");
        if (guards is null) return null;
        var row = new HoldReleaseMandateOffer { Id = id, ActorUserId = credential.ActorUserId.ToString("N"),
            TenantId = credential.TenantId?.ToString("N"), HoldRecordId = request.Terms.HoldRecordId,
            InputHash = inputHash, RequestJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request)),
            RequestHash = CanonicalJson.Hash(request), CreatedAt = DateTimeOffset.UtcNow };
        // raw: current authority and exact owned liability fence the immutable disclosure; this does not authorize custody.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(request.Terms.HoldRecordId)
            .Where(value => value.Version == hold.Version && value.State == "Held" && value.ActiveCommandId == null
                && value.ActiveReleaseMandateId == null && value.DefinitionHash == hold.DefinitionHash && value.DefinitionJson == hold.DefinitionJson
                && value.OwnerUserId == hold.OwnerUserId && value.TenantId == hold.TenantId)
            .Set(value => value.Version, hold.Version).Build(), "mandate_offer_hold", "_mandate_offer_hold"));
        statements.Add(SurrealQuery.Of("IF $_mandate_offer_hold = NONE OR $_mandate_offer_hold = [] { THROW 'HOLD_MANDATE_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(row), "mandate_offer_create"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var result = await executor.ExecuteAsync(query, ct); result.EnsureAllOk(); return result;
            }, ct);
            return row;
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains("HOLD_MANDATE_CHANGED", StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            existing = await GetOfferAsync(id, ct);
            return existing?.InputHash == inputHash ? existing : null;
        }
    }
}
