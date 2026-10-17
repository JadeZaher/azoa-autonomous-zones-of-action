using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealFungibleCreationStore(ISurrealExecutor executor) : IFungibleCreationStore
{
    private const string Conflict = "FUNGIBLE_CREATION_CHANGED";

    /// <inheritdoc/>
    public Task<FungibleCreationOperation?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<FungibleCreationOperation>(SurrealQuery<FungibleCreationOperation>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<FungibleCreationOperation?> AuthorizeAsync(FungibleCreationOperation expected,
        FungibleCreationAuthorization authorization, CancellationToken ct = default)
    {
        var previous = await executor.QuerySingleAsync<FungibleCreationAuthorization>(SurrealQuery<FungibleCreationAuthorization>.Key(authorization.Id), ct);
        if (previous is not null)
            return previous.OperationId == expected.Id && previous.RequestHash == authorization.RequestHash
                && previous.ActorId == expected.ActorUserId && previous.TenantId == expected.TenantId ? await GetAsync(expected.Id, ct) : null;
        var command = Command(expected);
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(expected.IntentJson)!;
        if (expected.State is not ("AwaitingConsent" or "Admitted" or "Preparing" or "Prepared" or "FailedNoEffect")
            || authorization.OperationId != expected.Id
            || authorization.ActorId != expected.ActorUserId || authorization.TenantId != expected.TenantId
            || authorization.ExpectedVersion != expected.Version
            || authorization.IntentHash != FungibleCreationIdentity.IntentHash(intent)
            || authorization.PreparedHash != (expected.PreparedJson is null ? null : CanonicalJson.HashCanonical(expected.PreparedJson))) return null;
        var fence = FungibleCreationAuthorityFence.Create(command, authorization.AuthorityJson, "fungible_renewal", freshParticipant: true);
        if (fence is null) return null;
        // raw: fresh current consent, original workflow and exact preserved operation/wire commit with immutable authorization audit.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fence);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<FungibleCreationOperation>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State && row.RequestHash == expected.RequestHash
                && row.IntentJson == expected.IntentJson && row.PreparedJson == expected.PreparedJson)
            .Set(row => row.Version, expected.Version).Build(), "fungible_renewal_lock", "_fungible_renewal_row"));
        statements.Add(SurrealQuery.Of("IF $_fungible_renewal_row = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
        var index = statements.Count;
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<FungibleCreationOperation>(expected.Id)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.CurrentAuthorityId, authorization.Id)
            .Set(row => row.CurrentAuthorityJson, authorization.AuthorityJson).Set(row => row.UpdatedAt, authorization.CreatedAt).Build(), "fungible_authority_pointer"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(authorization), "fungible_authority_audit"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(transaction, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return result.GetValues<FungibleCreationOperation>(index).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (IsConflict(exception))
        {
            var saved = await executor.QuerySingleAsync<FungibleCreationAuthorization>(SurrealQuery<FungibleCreationAuthorization>.Key(authorization.Id), ct);
            return saved is not null && saved.OperationId == expected.Id && saved.RequestHash == authorization.RequestHash
                && saved.ActorId == expected.ActorUserId && saved.TenantId == expected.TenantId ? await GetAsync(expected.Id, ct) : null;
        }
    }

    /// <inheritdoc/>
    public async Task<FungibleCreationOperation?> ClaimAsync(FungibleCreationOperation candidate, CancellationToken ct = default)
    {
        var existing = await GetAsync(candidate.Id, ct);
        if (existing is not null) return existing;
        if (candidate.CurrentAuthorityId is not null || candidate.CurrentAuthorityJson is not null) return null;
        // raw: quote admission, workflow authority and unique immutable intent commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(Authority(candidate, true));
        statements.AddRange(NodeGovernancePolicyFence.Create(Command(candidate).AdmissionPolicyVersion));
        if (candidate.State == "Admitted")
            statements.Add(SurrealQuery.Of("IF $_fungible_quote_expiry <= time::now() { THROW 'FUNGIBLE_CREATION_CHANGED' }")
                .WithParam("_fungible_quote_expiry", candidate.QuoteExpiresAt));
        else if (candidate.State != "AwaitingConsent") throw new ArgumentException("Invalid initial creation state.");
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(candidate), "fungible_admission"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(transaction, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return await GetAsync(candidate.Id, ct);
        }
        catch (SurrealStatementException exception) when (IsConflict(exception)) { return await GetAsync(candidate.Id, ct); }
    }

    /// <inheritdoc/>
    public async Task<FungibleCreationOperation?> SetQuoteAsync(FungibleCreationOperation expected, string quoteJson, bool accept,
        long? expectedPolicyVersion, CancellationToken ct = default)
    {
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(expected.IntentJson)!;
        var quote = JsonSerializer.Deserialize<FungibleCreationQuote>(quoteJson)!;
        var command = Command(expected);
        if (expected.State != "AwaitingConsent" || !FungibleCreationIdentity.IsValidQuote(intent with { Quote = quote })
            || (command.GenesisAuthority is { } genesis && quote.ProtocolIdentity != genesis.ProtocolIdentity)
            || (accept && expected.OfferedQuoteJson != quoteJson)) return null;
        var mutation = SurrealWriter.UpdateOnly<FungibleCreationOperation>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == "AwaitingConsent" && row.RequestHash == expected.RequestHash
                && row.IntentJson == expected.IntentJson && row.OfferedQuoteJson == expected.OfferedQuoteJson
                && row.CurrentAuthorityId == expected.CurrentAuthorityId && row.CurrentAuthorityJson == expected.CurrentAuthorityJson)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow)
            .Set(row => row.OfferedQuoteJson, quoteJson).Set(row => row.QuoteExpiresAt, quote.ExpiresAt);
        if (accept) mutation = mutation.Set(row => row.State, "Admitted").Set(row => row.AcceptedAt, DateTimeOffset.UtcNow)
            .Set(row => row.IntentJson, JsonSerializer.Serialize(intent with { Quote = quote }));
        // raw: exact displayed quote acceptance, expiry and live workflow authority serialize with the request CAS.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(Authority(expected, true));
        statements.AddRange(NodeGovernancePolicyFence.Create(expectedPolicyVersion));
        if (accept) statements.Add(SurrealQuery.Of("IF $_fungible_quote_expiry <= time::now() { THROW 'FUNGIBLE_CREATION_CHANGED' }")
            .WithParam("_fungible_quote_expiry", quote.ExpiresAt));
        var index = statements.Count;
        statements.Add(SurrealGeneratedStatementScope.Apply(mutation.Build(), "fungible_quote"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(transaction, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return result.GetValues<FungibleCreationOperation>(index).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (IsConflict(exception)) { return null; }
    }

    /// <inheritdoc/>
    public async Task<FungibleCreationOperation?> UpdateAsync(FungibleCreationOperation candidate, long expectedVersion,
        string expectedState, bool authorizeEffect, long? expectedPolicyVersion, CancellationToken ct = default)
    {
        var expectedAuthorization = (expectedState, candidate.State) switch
        {
            ("Admitted", "Preparing") or ("Preparing", "Preparing" or "Prepared") or ("Prepared", "Submitted") => true,
            ("Submitted" or "NeedsOperator", "Confirmed" or "FailedNoEffect" or "NeedsOperator") => false,
            _ => (bool?)null,
        };
        if (expectedAuthorization is null || expectedAuthorization != authorizeEffect || expectedVersion == long.MaxValue)
            throw new ArgumentException("Creation transition or effect authorization is invalid.");
        var mutation = SurrealWriter.UpdateOnly<FungibleCreationOperation>(candidate.Id)
            .Where(row => row.Version == expectedVersion && row.State == expectedState && row.RequestHash == candidate.RequestHash
                && row.RequestJson == candidate.RequestJson && row.IntentJson == candidate.IntentJson
                && row.Generation == candidate.Generation && row.HistoryJson == candidate.HistoryJson
                && row.CurrentAuthorityId == candidate.CurrentAuthorityId && row.CurrentAuthorityJson == candidate.CurrentAuthorityJson)
            .Set(row => row.State, candidate.State).Set(row => row.Version, expectedVersion + 1)
            .Set(row => row.UpdatedAt, candidate.UpdatedAt);
        mutation = candidate.PreparedJson is null ? mutation.Unset(row => row.PreparedJson) : mutation.Set(row => row.PreparedJson, candidate.PreparedJson);
        mutation = candidate.ObservationJson is null ? mutation.Unset(row => row.ObservationJson) : mutation.Set(row => row.ObservationJson, candidate.ObservationJson);
        mutation = candidate.AssetId is null ? mutation.Unset(row => row.AssetId) : mutation.Set(row => row.AssetId, candidate.AssetId);
        mutation = candidate.Detail is null ? mutation.Unset(row => row.Detail) : mutation.Set(row => row.Detail, candidate.Detail);
        var statements = new List<SurrealQuery>();
        if (authorizeEffect)
        {
            // raw: current action/tenant/policy authority and the new effect authorization serialize together.
            statements.Add(SurrealQuery.Of("BEGIN"));
            statements.AddRange(Authority(candidate, true));
            statements.AddRange(NodeGovernancePolicyFence.Create(expectedPolicyVersion));
        }
        var index = statements.Count;
        statements.Add(SurrealGeneratedStatementScope.Apply(mutation.Build(), "fungible_progress"));
        if (authorizeEffect) statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            var result = await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(transaction, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return result.GetValues<FungibleCreationOperation>(index).SingleOrDefault();
        }
        catch (SurrealStatementException exception) when (IsConflict(exception)) { return null; }
    }

    /// <inheritdoc/>
    public async Task<bool> CompleteAsync(FungibleCreationOperation expected, FungibleAssetAuthority authority,
        PlatformReserveLedger? reserve, CancellationToken ct = default)
    {
        var command = Command(expected);
        var observation = JsonSerializer.Deserialize<FungibleCreationObservation>(expected.ObservationJson ?? "null");
        if (expected.State != "Confirmed" || observation is not { Verdict: "Confirmed", AssetId: not null }
            || expected.AssetId != observation.AssetId || authority.OperationId != expected.Id || authority.AssetId != expected.AssetId
            || authority.IntentJson != expected.IntentJson || authority.CreationTransactionId != observation.TransactionId
            || authority.ProtocolIdentity != observation.ProtocolIdentity || authority.IssuerUserId != expected.ActorUserId
            || authority.TenantId != expected.TenantId || authority.ChainType != command.Intent.ChainType
            || authority.Network != command.Intent.Network.ToString() || authority.CreatorAddress != command.Intent.CreatorAddress
            || authority.IntentHash != CanonicalJson.Hash(JsonSerializer.Deserialize<FungibleCreationIntent>(expected.IntentJson)!) || authority.EvidenceJson != observation.EvidenceJson
            || string.IsNullOrWhiteSpace(observation.EvidenceJson)) return false;
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(expected.IntentJson)!;
        if ((command.MintTarget == "PlatformReserve") != (reserve is not null)
            || reserve is not null && (reserve.AssetId != authority.AssetId || reserve.Chain != authority.ChainType
                || reserve.Network != authority.Network || reserve.ProtocolIdentity != authority.ProtocolIdentity
                || reserve.ReserveAddress != intent.CreatorAddress || reserve.TotalSupply != intent.TotalBaseUnits
                || reserve.UnitName != intent.UnitName || reserve.Decimals != intent.Decimals
                || SurrealRecordGuid.ParseOptional(reserve.TenantId) != command.TenantId)) return false;
        var previousReserve = reserve is null ? null : await executor.QuerySingleAsync<PlatformReserveLedger>(SurrealQuery<PlatformReserveLedger>.Key(reserve.Id), ct);
        if (previousReserve is not null && !ReserveAssetRegistrationPlan.SameIdentity(previousReserve, reserve!)) return false;
        var genesisRow = command.GenesisAuthority is { } genesisPurpose
            ? await executor.QuerySingleAsync<PlatformToken>(SurrealQuery<PlatformToken>.Key(genesisPurpose.PlatformTokenId), ct) : null;
        if (command.GenesisAuthority is { } genesis && (genesisRow is null || authority.ProtocolIdentity != genesis.ProtocolIdentity)) return false;
        // raw: confirmed operation CAS, unique issuance, reserve registration and optional holon link commit atomically.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(Authority(expected, false));
        if (command.GenesisAuthority is { } purpose)
        {
            statements.AddRange(PlatformGenesisFence.Definition(command, purpose));
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<PlatformToken>(purpose.PlatformTokenId)
                .Where(row => row.Version == genesisRow!.Version && row.Status == "Pending" && row.CreationOperationId == expected.Id)
                .Set(row => row.Status, "Active").Set(row => row.AssetId, authority.AssetId).Set(row => row.Version, genesisRow!.Version + 1)
                .Set(row => row.ActivatedAt, DateTimeOffset.UtcNow).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow)
                .Unset(row => row.LastFailureReason).Build(), "fungible_genesis_activation", "_fungible_genesis_activation"));
            statements.Add(SurrealQuery.Of("IF $_fungible_genesis_activation = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<FungibleCreationOperation>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == "Confirmed" && row.RequestHash == expected.RequestHash
                && row.IntentJson == expected.IntentJson && row.ObservationJson == expected.ObservationJson)
            .Set(row => row.State, "Completed").Set(row => row.Version, expected.Version + 1)
            .Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), "fungible_completion", "_fungible_won"));
        statements.Add(SurrealQuery.Of("IF $_fungible_won = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(authority), "fungible_authority"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(new FungibleCreationProjection
        {
            Id = expected.Id, AuthorityId = authority.Id, RequestHash = expected.RequestHash, HolonId = command.HolonId?.ToString("N"),
            InitialAttachmentState = command.HolonId is null ? "NotRequested" : "Pending", CreatedAt = DateTimeOffset.UtcNow,
        }), "fungible_projection"));
        if (reserve is not null)
        {
            if (previousReserve is null) statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(reserve), "fungible_reserve"));
            else
            {
                statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<PlatformReserveLedger>(reserve.Id)
                    .Where(row => row.Version == previousReserve.Version).Set(row => row.Version, previousReserve.Version).Build(),
                    "fungible_existing_reserve", "_fungible_reserve"));
                statements.Add(SurrealQuery.Of("IF $_fungible_reserve = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
            }
        }
        if (command.HolonId is { } holon)
        {
            statements.Add(LocalEntityMutationFence.Holons());
            var actorLink = SurrealLink.ToLink("user", expected.ActorUserId);
            statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(holon.ToString("N"))
                .Where(row => row.IsActive && row.UserId == actorLink && (row.TokenId == null || row.TokenId == "")
                    && (row.ChainId == null || row.ChainId == "" || row.ChainId == authority.ChainType)
                    && (row.AssetType == null || row.AssetType != ConsumerGovernanceHolonTypes.ProposalAssetType))
                .Set(row => row.TokenId, authority.AssetId).Set(row => row.ChainId, authority.ChainType)
                .Set(row => row.ModifiedDate, DateTimeOffset.UtcNow).Build(), "fungible_holon_link", "_fungible_link_result"));
            statements.Add(SurrealQuery.Of("""
                UPDATE ONLY type::record('fungible_creation_projection', $_fungible_id)
                    SET initial_attachment_state = IF $_fungible_link_result = NONE { 'Pending' } ELSE { 'Attached' }
                """).WithParam("_fungible_id", expected.Id));
            statements.Add(SurrealQuery.Of("""
                UPDATE ONLY type::record('fungible_creation_operation', $_fungible_id)
                    SET state = IF $_fungible_link_result = NONE { 'AttachmentPending' } ELSE { 'Completed' },
                        detail = IF $_fungible_link_result = NONE { $_fungible_pending_detail } ELSE { NONE }
                """).WithParam("_fungible_id", expected.Id)
                .WithParam("_fungible_pending_detail", "Issuance is finalized and accounted for; the requested Holon requires current owner attachment authorization."));

        }
        statements.Add(SurrealQuery.Of("COMMIT"));
        var transaction = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await SurrealTransientConflict.RetryOnConflictAsync(async () =>
            {
                var response = await executor.ExecuteAsync(transaction, ct);
                response.EnsureAllOk();
                return response;
            }, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (IsConflict(exception)) { return false; }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FungibleCreationOperation>> ListParticipantAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default)
    {
        var actorId = actor.ToString("N");
        var tenantId = tenant?.ToString("N");
        return executor.QueryAsync<FungibleCreationOperation>(SurrealQuery<FungibleCreationOperation>.From()
            .Where(row => row.ActorUserId == actorId && row.TenantId == tenantId)
            .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 51)).Start(Math.Max(0, offset)), ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FungibleCreationOperation>> ListRecoverableAsync(int limit, CancellationToken ct = default)
        => executor.QueryAsync<FungibleCreationOperation>(SurrealQuery<FungibleCreationOperation>.From()
            .Where(row => row.State == "Submitted" || row.State == "Confirmed" || row.State == "NeedsOperator")
            .OrderBy(row => row.UpdatedAt).Limit(Math.Clamp(limit, 1, 100)), ct);

    /// <inheritdoc/>
    public async Task TouchRecoveryAsync(FungibleCreationOperation expected, CancellationToken ct = default)
    {
        if (expected.State is not ("Submitted" or "Confirmed" or "NeedsOperator")) return;
        var result = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<FungibleCreationOperation>(expected.Id)
            .Where(row => row.Version == expected.Version && row.State == expected.State)
            .Set(row => row.Version, expected.Version + 1).Set(row => row.UpdatedAt, DateTimeOffset.UtcNow).Build(), ct);
        result.EnsureAllOk();
    }

    private static FungibleCreationCommand Command(FungibleCreationOperation row)
    {
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson) ?? throw new InvalidOperationException("Creation command is missing.");
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        if (CanonicalJson.Hash(command) != row.RequestHash || command.ActorUserId.ToString("N") != row.ActorUserId
            || command.TenantId?.ToString("N") != row.TenantId
            || intent.Generation != row.Generation || row.Generation < 0
            || FungibleCreationIdentity.IntentHash(command.Intent) != FungibleCreationIdentity.IntentHash(intent with { Generation = 0 })
            || (row.Generation == 0 && command.Intent.Quote is not null && CanonicalJson.Hash(command.Intent) != CanonicalJson.Hash(intent)))
            throw new InvalidOperationException("Creation authority conflicts with the saved command.");
        var history = JsonSerializer.Deserialize<List<FungibleCreationGeneration>>(row.HistoryJson);
        if (history is null || history.Count != row.Generation || history.Where((entry, index) => entry.Generation != index).Any())
            throw new InvalidOperationException("Creation generation history conflicts with the saved command.");
        if (row.State != "AwaitingConsent" && !FungibleCreationIdentity.IsValidQuote(intent))
            throw new InvalidOperationException("Creation admission lacks its exact accepted quote.");
        return command;
    }

    // raw: exact credential, selected grants, owned wallet and original workflow fence join new effect authorization.
    private static IReadOnlyList<SurrealQuery> Authority(FungibleCreationOperation row, bool action)
    {
        var command = Command(row);
        if (!action) return [];
        var fence = FungibleCreationAuthorityFence.Create(command, row.CurrentAuthorityJson, "fungible_signing");
        if (fence is null) return [SurrealQuery.Of("THROW 'FUNGIBLE_CREATION_CHANGED'")];
        var statements = fence.ToList();
        if (row.CurrentAuthorityId is { } authorizationId)
        {
            statements.Add(SurrealQuery.Of("LET $_fungible_authorization = (SELECT * FROM ONLY type::record('fungible_creation_authorization', $_fungible_authorization_id))")
                .WithParam("_fungible_authorization_id", authorizationId));
            statements.Add(SurrealQuery.Of("""
                IF $_fungible_authorization = NONE OR $_fungible_authorization.operation_id != $_fungible_operation_id
                    OR $_fungible_authorization.authority_json != $_fungible_current_authority
                    OR $_fungible_authorization.intent_hash != $_fungible_current_intent
                    { THROW 'FUNGIBLE_CREATION_CHANGED' }
                """).WithParam("_fungible_operation_id", row.Id).WithParam("_fungible_current_authority", row.CurrentAuthorityJson)
                .WithParam("_fungible_current_intent", FungibleCreationIdentity.IntentHash(JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!)));
        }
        else if (row.CurrentAuthorityJson is not null) return [SurrealQuery.Of("THROW 'FUNGIBLE_CREATION_CHANGED'")];
        return statements;
    }

    private static bool IsConflict(SurrealStatementException error) => SurrealIdempotencyLedger.IsUniqueViolation(error.Message)
        || error.Message.Contains(Conflict, StringComparison.Ordinal) || error.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
        || error.Message.Contains(NodeGovernancePolicyFence.FailureMarker, StringComparison.Ordinal)
        || error.Message.Contains(PlatformGenesisFence.FailureMarker, StringComparison.Ordinal)
        || error.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal);
}
