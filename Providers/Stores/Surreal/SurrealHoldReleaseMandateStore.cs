using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>Persists exact automatic-release permission separately from liability and chain settlement.</summary>
public sealed partial class SurrealHoldReleaseMandateStore(ISurrealExecutor executor) : IHoldReleaseMandateStore
{
    /// <inheritdoc/>
    public Task<HoldReleaseMandate?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<HoldReleaseMandate>(SurrealQuery<HoldReleaseMandate>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<HoldReleaseMandate?> AdmitAsync(HoldReleaseMandateAcceptance acceptance, SigningConsentWitness authority,
        CancellationToken ct = default)
    {
        if (acceptance is null || string.IsNullOrWhiteSpace(acceptance.OfferId) || string.IsNullOrWhiteSpace(acceptance.RequestHash)) return null;
        var offer = await GetOfferAsync(acceptance.OfferId, ct);
        if (offer is null || offer.ActorUserId != authority.Credential.ActorUserId.ToString("N")
            || offer.TenantId != authority.Credential.TenantId?.ToString("N") || offer.RequestHash != acceptance.RequestHash
            || offer.RequestHash != CanonicalJson.HashCanonical(offer.RequestJson)) return null;
        var request = JsonSerializer.Deserialize<HoldReleaseMandateRequest>(offer.RequestJson)!;
        if (request.RequestId == Guid.Empty || request.Terms is null) return null;
        var credential = authority.Credential;
        var id = HoldReleaseMandateIdentity.Id(credential.ActorUserId, credential.TenantId, request.RequestId);
        var requestHash = CanonicalJson.Hash(request);
        var existing = await GetAsync(id, ct);
        if (existing is not null) return existing.ActorUserId == credential.ActorUserId.ToString("N")
            && existing.TenantId == credential.TenantId?.ToString("N") && existing.RequestHash == requestHash ? existing : null;
        var hold = await executor.QuerySingleAsync<HoldLedger>(SurrealQuery<HoldLedger>.Key(request.Terms.HoldRecordId), ct);
        if (hold is null || hold.Version is < 0 or long.MaxValue || hold.OwnerUserId != credential.ActorUserId.ToString("N")
            || SurrealRecordGuid.ParseOptional(hold.TenantId) != credential.TenantId || !HoldReleaseMandateIdentity.ValidTerms(hold, request)) return null;
        var guards = SigningAuthorityFence.ForConsent(authority, [AzoaScopes.TransferSign], "hold_mandate_admit");
        if (guards is null) return null;
        var terms = request.Terms; var now = DateTimeOffset.UtcNow;
        var original = await executor.QuerySingleAsync<HoldCommandReceipt>(SurrealQuery<HoldCommandReceipt>.Key(hold.OpeningCommandId!), ct);
        if (original is null) return null;
        var liability = JsonSerializer.Deserialize<HoldCommandView>(original.OutcomeJson);
        if (liability is null || liability.State != "Completed" || liability.OperationId != hold.OpeningCommandId
            || (liability.HoldRecordId != terms.HoldRecordId && liability.NextHoldRecordId != terms.HoldRecordId)
            || liability.Asset != terms.Asset || liability.QuantityBaseUnits != terms.QuantityBaseUnits
            || liability.OwnerUserId != credential.ActorUserId || liability.CustodyTenantId != SurrealRecordGuid.ParseOptional(hold.TenantId)) return null;
        var row = new HoldReleaseMandate { Id = id, ActorUserId = credential.ActorUserId.ToString("N"),
            TenantId = credential.TenantId?.ToString("N"), HoldRecordId = terms.HoldRecordId, RequestHash = requestHash,
            TermsJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(terms)), TermsHash = CanonicalJson.Hash(terms),
            OriginalAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            CurrentAuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            OriginalQuoteJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(request.Quote)),
            NotBefore = terms.NotBefore, ExpiresAt = terms.ExpiresAt, NextAttemptAt = terms.NotBefore,
            CreatedAt = now, UpdatedAt = now };
        // raw: current owner consent, exact existing liability, recipient account and unique mandate activation commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealQuery.Of("IF $_mandate_expiry <= time::now() OR $_mandate_quote_expiry <= time::now() { THROW 'HOLD_MANDATE_CHANGED' }")
            .WithParam("_mandate_expiry", row.ExpiresAt).WithParam("_mandate_quote_expiry", request.Quote.ExpiresAt));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealQuery<HoldCommandReceipt>.Key(hold.OpeningCommandId!),
            "mandate_original", "_mandate_original"));
        statements.Add(SurrealQuery.Of("IF $_mandate_original = NONE OR $_mandate_original.hold_record_id != $_mandate_hold OR $_mandate_original.outcome_json != $_mandate_outcome { THROW 'HOLD_MANDATE_CHANGED' }")
            .WithParam("_mandate_hold", original.HoldRecordId).WithParam("_mandate_outcome", original.OutcomeJson));
        var owner = SurrealLink.ToLink("user", terms.Recipient.OwnerUserId.ToString("N"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<Wallet>(terms.Recipient.Id.ToString("N"))
            .Where(value => value.UserId == owner && value.Address == terms.Recipient.Address && value.ChainType == terms.Recipient.ChainType)
            .Set(value => value.Address, terms.Recipient.Address).Build(), "mandate_recipient", "_mandate_recipient"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<HoldLedger>(terms.HoldRecordId)
            .Where(value => value.Version == hold.Version && value.State == "Held" && value.ActiveCommandId == null
                && value.ActiveReleaseMandateId == null && value.DefinitionJson == hold.DefinitionJson && value.DefinitionHash == hold.DefinitionHash
                && value.OwnerUserId == hold.OwnerUserId && value.TenantId == hold.TenantId && value.OpeningCommandId == hold.OpeningCommandId)
            .Set(value => value.ActiveReleaseMandateId, id).Set(value => value.Version, hold.Version + 1).Set(value => value.UpdatedAt, now).Build(),
            "mandate_hold", "_mandate_hold_result"));
        statements.Add(SurrealQuery.Of("IF $_mandate_recipient = NONE OR $_mandate_recipient = [] OR $_mandate_hold_result = NONE OR $_mandate_hold_result = [] { THROW 'HOLD_MANDATE_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(row), "mandate_create"));
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
            existing = await GetAsync(id, ct);
            return existing?.RequestHash == requestHash ? existing : null;
        }
    }
}
