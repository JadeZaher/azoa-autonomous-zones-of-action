using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealWttOperationStore(ISurrealExecutor executor, TimeProvider? clock = null) : IWttOperationStore
{
    private const string ConflictMarker = "WTT_JOURNAL_CONFLICT";
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    /// <inheritdoc/>
    public Task<WttOffer?> GetOfferAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<WttOffer>(SurrealQuery<WttOffer>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<WttOffer?> CreateOfferAsync(WttOffer offer, CancellationToken ct = default)
    {
        if (!ValidOffer(offer)) throw new ArgumentException("Invalid immutable WTT offer.", nameof(offer));
        try
        {
            var response = await executor.ExecuteAsync(SurrealWriter.Create(offer), ct);
            response.EnsureAllOk();
            return response.GetValues<WttOffer>(0).Single();
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<WttOperation?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<WttOperation>(SurrealQuery<WttOperation>.Key(id), ct);

    /// <inheritdoc/>
    public Task<WttOperationStage?> GetStageAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<WttOperationStage>(SurrealQuery<WttOperationStage>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WttOperationStage>> ReadStagesAsync(string operationId, CancellationToken ct = default)
    {
        var operation = await GetAsync(operationId, ct);
        if (operation is null) return [];
        var rows = await executor.QueryAsync<WttOperationStage>(SurrealQuery<WttOperationStage>.From()
            .Where(row => row.OperationId == operationId).Limit(512), ct);
        var byId = rows.Where(row => row.IntentHash == operation.IntentHash)
            .ToDictionary(row => SurrealRecordGuid.BareId(row.Id), StringComparer.Ordinal);
        var chain = new List<WttOperationStage>();
        var cursor = operation.CurrentStageId;
        while (cursor is not null && chain.Count < 256)
        {
            if (!byId.Remove(cursor, out var stage)) throw new InvalidOperationException("WTT stage ancestry is incomplete or cyclic.");
            chain.Add(stage);
            cursor = stage.PreviousStageId;
        }
        if (cursor is not null) throw new InvalidOperationException("WTT stage ancestry exceeds its bounded history.");
        chain.Reverse();
        return chain;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WttOperation>> ListAsync(string actorUserId, string tenantId, DateTimeOffset? before, int limit,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || string.IsNullOrEmpty(actorUserId)) return [];
        var query = before is { } cursor
            ? SurrealQuery<WttOperation>.From().Where(row => row.ActorUserId == actorUserId && row.TenantId == tenantId && row.CreatedAt < cursor)
            : SurrealQuery<WttOperation>.From().Where(row => row.ActorUserId == actorUserId && row.TenantId == tenantId);
        return await executor.QueryAsync<WttOperation>(query.OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Limit(limit), ct);
    }

    /// <inheritdoc/>
    public async Task<WttOperation?> AdmitAsync(WttOperation candidate, WttStageAuthority authority, CancellationToken ct = default)
    {
        var intent = ValidateCandidate(candidate);
        if (intent is null || !SamePrincipal(intent, authority.Signing?.Credential, false)) return null;
        var existing = await GetAsync(candidate.Id, ct);
        if (existing is not null) return SameRequest(existing, candidate) ? existing : null;
        var guards = WttSigningAuthorityFence.Stage(intent, "Source", null, authority, "wtt_fence_admission");
        if (guards is null) return null;
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        // raw: offer consumption/expiry, current source authority and operation creation commit together.
        statements.Add(SurrealQuery.Of("""
            LET $_wtt_offer = (UPDATE ONLY type::record('wtt_offer', $_wtt_offer_id) SET consumed_by_operation_id = $_wtt_operation_id
                WHERE actor_user_id = $_wtt_actor AND tenant_id = $_wtt_tenant AND offer_hash = $_wtt_offer_hash
                    AND operation_id = $_wtt_operation_id AND intent_hash = $_wtt_intent_hash
                    AND consumed_by_operation_id = NONE AND expires_at > time::now() RETURN AFTER)
            """).WithParam("_wtt_offer_id", candidate.OfferId).WithParam("_wtt_operation_id", candidate.Id)
            .WithParam("_wtt_actor", candidate.ActorUserId).WithParam("_wtt_tenant", candidate.TenantId)
            .WithParam("_wtt_offer_hash", candidate.OfferHash).WithParam("_wtt_intent_hash", candidate.IntentHash));
        statements.Add(SurrealQuery.Of("IF $_wtt_offer = NONE { THROW 'WTT_JOURNAL_CONFLICT' }"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.Create(Clone(candidate)), "wtt_gen_admitted", "_wtt_admitted"));
        var admittedIndex = statements.Count;
        statements.Add(SurrealQuery.Of("RETURN $_wtt_admitted"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await ExecuteAsync(statements, ct);
            return response.GetValues<WttOperation>(admittedIndex).Single();
        }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception))
        {
            var winner = await GetAsync(candidate.Id, ct);
            return winner is not null && SameRequest(winner, candidate) ? winner : null;
        }
    }

    /// <inheritdoc/>
    public async Task<WttOperation?> CancelAsync(WttOperation candidate, WttStageAuthority authority, long? probedVersion = null,
        CancellationToken ct = default)
    {
        var intent = ValidateCandidate(candidate);
        if (intent is null || !SamePrincipal(intent, authority.Consent?.Credential, true)) return null;
        var guards = WttSigningAuthorityFence.Stage(intent, "Cancel", null, authority, "wtt_fence_cancel");
        if (guards is null) return null;
        var existing = await GetAsync(candidate.Id, ct);
        if (existing is null)
        {
            var tombstone = Clone(candidate);
            tombstone.State = "Cancelled";
            tombstone.Detail = "Cancelled before admission; the original offer was never consumed.";
            var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
            statements.AddRange(guards);
            // raw: current consent and the admission tombstone commit together.
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.Create(tombstone), "wtt_gen_tombstone", "_wtt_tombstone"));
            var tombstoneIndex = statements.Count;
            statements.Add(SurrealQuery.Of("RETURN $_wtt_tombstone"));
            statements.Add(SurrealQuery.Of("COMMIT"));
            try
            {
                var response = await ExecuteAsync(statements, ct);
                return response.GetValues<WttOperation>(tombstoneIndex).Single();
            }
            catch (SurrealStatementException exception) when (ExpectedConflict(exception))
            {
                existing = await GetAsync(candidate.Id, ct);
                if (existing is null) return null;
            }
        }
        if (!SameRequest(existing, candidate)) return null;
        if (existing.State == "Cancelled") return existing;
        if (existing.Phase != "Source" || existing.TransferPublicationJson is not null
            || existing.State is not ("Admitted" or "Ready" or "Active")) return null;
        // A durable-nonce journal cancels only at the exact version whose reservation rows the caller probed.
        if (probedVersion is { } probed && probed != existing.Version
            || intent.Source.ChainType == "Ethereum" && existing.CurrentStageId is not null && probedVersion is null) return null;
        var current = existing.CurrentStageId is { } stageId ? await GetStageAsync(stageId, ct) : null;
        if (existing.CurrentStageId is not null && (current is null || current.OperationId != candidate.Id)) return null;
        // Version-pinned stage set: a sent or disclosed Transfer without proven no-effect may already have published.
        if ((await ReadStagesAsync(candidate.Id, ct)).Any(stage => stage.Phase == "Source" && stage.Stage == "Transfer"
                && (stage.AuthorizedAt is not null || stage.ExposedAt is not null) && stage.State is not ("FailedOnChain" or "ExpiredNoEffect")))
            return null;
        SurrealQuery? stageMutation = null;
        var now = Now;
        if (current is not null && !WttStageStates.IsTerminal(current.State))
        {
            // Preparing may already hold a reserved nonce; a signed EVM candidate owns its nonce until it resolves on chain.
            if (current.Phase != "Source" || current.AuthorizedAt is not null || current.ExposedAt is not null
                || current.State is not ("Planned" or "Prepared")
                || intent.Source.ChainType == "Ethereum" && current.State == "Prepared") return null;
            var stageVersion = current.Version;
            var stageState = current.State;
            stageMutation = SurrealWriter.UpdateOnly<WttOperationStage>(SurrealRecordGuid.BareId(current.Id))
                .Where(row => row.Version == stageVersion && row.State == stageState && row.AuthorizedAt == null && row.ExposedAt == null)
                .Set(row => row.State, "Cancelled").Set(row => row.Version, stageVersion + 1).Set(row => row.UpdatedAt, now).Build();
        }
        var version = existing.Version;
        var state = existing.State;
        var operationMutation = SurrealWriter.UpdateOnly<WttOperation>(candidate.Id)
            .Where(row => row.Version == version && row.State == state && row.TransferPublicationJson == null)
            .Set(row => row.State, "Cancelled").Set(row => row.Detail, "Cancelled before any source publication.")
            .Set(row => row.Version, version + 1).Set(row => row.UpdatedAt, now).Build();
        var committed = stageMutation is null ? await CommitOperationAsync(guards, operationMutation, ct)
            : await CommitAsync(guards, operationMutation, stageMutation, ct) is not null;
        return committed ? await GetAsync(candidate.Id, ct) : null;
    }

    private async Task<bool> CommitOperationAsync(IReadOnlyList<SurrealQuery> guards, SurrealQuery operationMutation, CancellationToken ct)
    {
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        // raw: current consent and the operation-only transition commit together.
        statements.Add(SurrealGeneratedStatementScope.Assign(operationMutation, "wtt_gen_operation", "_wtt_operation_saved"));
        statements.Add(SurrealQuery.Of("IF $_wtt_operation_saved = NONE OR $_wtt_operation_saved = [] { THROW 'WTT_JOURNAL_CONFLICT' }"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            await ExecuteAsync(statements, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (ExpectedConflict(exception))
        {
            return false;
        }
    }

    private async Task<SurrealResponse> ExecuteAsync(IReadOnlyList<SurrealQuery> statements, CancellationToken ct)
    {
        var query = SurrealQuery.Combine(statements.ToArray());
        return await SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return response;
        }, ct);
    }

    private static bool ExpectedConflict(SurrealStatementException exception)
        => SurrealIdempotencyLedger.IsUniqueViolation(exception.Message)
            || exception.Message.Contains(ConflictMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal);

    private static bool SamePrincipal(WttOperationIntent intent, SigningCredentialWitness? credential, bool allowDirectOwner)
        => credential is not null && credential.ActorUserId == intent.ActorUserId && credential.TenantId != Guid.Empty
            && (credential.TenantId == intent.TenantId || (allowDirectOwner && credential.TenantId is null));

    private static bool SameRequest(WttOperation left, WttOperation right)
        => left.ActorUserId == right.ActorUserId && left.TenantId == right.TenantId && left.RequestKeyHash == right.RequestKeyHash
            && left.OfferId == right.OfferId && left.OfferHash == right.OfferHash && left.IntentHash == right.IntentHash
            && left.IntentJson == right.IntentJson && left.SourceQuoteJson == right.SourceQuoteJson
            && left.ReturnOfOperationId == right.ReturnOfOperationId;

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    internal static WttOperationIntent Intent(WttOperation operation) => JsonSerializer.Deserialize<WttOperationIntent>(operation.IntentJson)!;

    private static bool ValidOffer(WttOffer offer)
        => offer is not null && Guid.TryParseExact(offer.Id, "N", out var id) && id != Guid.Empty
            && Guid.TryParseExact(offer.ActorUserId, "N", out _) && (offer.TenantId.Length == 0 || Guid.TryParseExact(offer.TenantId, "N", out _))
            && WttOperationIdentity.IsCanonicalId(offer.OperationId) && WttOperationIdentity.IsCanonicalId(offer.RequestKeyHash)
            && WttOperationIdentity.IsCanonicalId(offer.OfferHash) && Canonical(offer.IntentJson, offer.IntentHash)
            && Canonical(offer.SourceQuoteJson, null) && Canonical(offer.TargetInspectionJson, null)
            && offer.ConsumedByOperationId is null && offer.ExpiresAt > offer.CreatedAt;

    private static WttOperationIntent? ValidateCandidate(WttOperation candidate)
    {
        if (candidate is null || candidate.Version != 0 || candidate.State != "Admitted" || candidate.Phase != "Source"
            || candidate.CurrentStageId is not null || candidate.TargetQuoteJson is not null || candidate.TransferPublicationJson is not null
            || candidate.AttestationPublicationJson is not null || candidate.TransferEnvelopeBase64 is not null
            || candidate.AttestationEnvelopeBase64 is not null || candidate.ProofsJson is not null || candidate.BodyClaimId is not null
            || candidate.SettlementJson is not null || !WttOperationIdentity.IsCanonicalId(candidate.Id)
            || !WttOperationIdentity.IsCanonicalId(candidate.RequestKeyHash) || !WttOperationIdentity.IsCanonicalId(candidate.OfferHash)
            || !Guid.TryParseExact(candidate.OfferId, "N", out _) || !Canonical(candidate.IntentJson, candidate.IntentHash)
            || !Canonical(candidate.SourceQuoteJson, null))
            return null;
        try
        {
            var intent = JsonSerializer.Deserialize<WttOperationIntent>(candidate.IntentJson);
            var quote = JsonSerializer.Deserialize<WttStageQuote>(candidate.SourceQuoteJson);
            if (intent is null || quote is null || intent.Version != 1 || intent.ActorUserId == Guid.Empty || intent.TenantId == Guid.Empty
                || intent.OperationId != candidate.Id || intent.ActorUserId.ToString("N") != candidate.ActorUserId
                || (intent.TenantId?.ToString("N") ?? "") != candidate.TenantId || CanonicalJson.Hash(intent) != candidate.IntentHash
                || WttOperationIdentity.Operation(intent.ActorUserId, intent.TenantId, candidate.RequestKeyHash) != candidate.Id
                || quote.IntentHash != candidate.IntentHash || quote.Phase != "Source"
                || quote.ProtocolIdentity != intent.Source.ProtocolIdentity)
                return null;
            return intent;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Canonical(string json, string? expectedHash)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return CanonicalJson.Canonicalize(document.RootElement) == json
                && (expectedHash is null || CanonicalJson.HashCanonical(json) == expectedHash);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
