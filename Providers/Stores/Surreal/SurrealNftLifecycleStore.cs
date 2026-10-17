using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealNftLifecycleStore(ISurrealExecutor executor) : INftLifecycleStore
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<NftLifecycle>> ListParticipantSnapshotAsync(Guid participant, DateTimeOffset snapshot, int offset, int limit, CancellationToken ct = default)
    {
        var id = participant.ToString("N");
        return executor.QueryAsync<NftLifecycle>(SurrealQuery<NftLifecycle>.From()
            .Where(row => (row.ActorUserId == id || row.RecipientUserId == id) && row.CreatedAt <= snapshot)
            .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id).Limit(Math.Clamp(limit, 1, 51)).Start(offset), ct);
    }
    /// <inheritdoc/>
    public async Task<CredentialOwnershipRecord?> FindCredentialAsync(string chain, string network, string assetId, string? contractAddress, CancellationToken ct = default)
    {
        var query = SurrealQuery<NftAssetAuthority>.From().Where(row => row.ChainType == chain
            && row.Network == network && row.AssetId == assetId && !row.Burned && !row.Revoked);
        if (!string.IsNullOrWhiteSpace(contractAddress)) query = query.Where(row => row.ContractAddress == contractAddress);
        var rows = await executor.QueryAsync<NftAssetAuthority>(query.Limit(2), ct);
        if (rows.Count != 1) return null;
        var authority = rows[0];
        var issuance = await GetAsync(authority.IssuanceLifecycleId, ct);
        var verified = issuance is { State: "Completed", Operation: "Mint" }
            && issuance.ActorUserId == authority.IssuerUserId && issuance.ActorUserId != issuance.RecipientUserId
            && issuance.NftId == authority.NftId && issuance.ChainType == authority.ChainType && issuance.Network == authority.Network
            && issuance.ProtocolIdentity == authority.ProtocolIdentity && issuance.ContractAddress == authority.ContractAddress
            && issuance.AssetId == authority.AssetId && issuance.CreationTransactionId == authority.CreationTransactionId
            && !string.IsNullOrWhiteSpace(issuance.FinalTransactionId);
        return new CredentialOwnershipRecord
        {
            Id = Guid.Parse(authority.NftId), HolderUserId = Guid.Parse(authority.HolderUserId), HolderAddress = authority.OwnerAddress,
            IssuerUserId = Guid.TryParse(authority.IssuerUserId, out var issuer) ? issuer : null,
            IsSoulbound = authority.Soulbound, ChainType = chain, TokenId = assetId, HasVerifiedIssuance = verified,
            Network = authority.Network, ProtocolIdentity = authority.ProtocolIdentity, ContractAddress = authority.ContractAddress,
        };
    }
    /// <inheritdoc/>
    public async Task<bool> ReserveAuthorityAsync(NftAssetAuthority authority, NftLifecycle lifecycle, CancellationToken ct = default)
    {
        // raw: an active lifecycle version and asset reservation must be acquired atomically; see AGENTS.md.
        var query = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"),
            SurrealQuery.Of("LET $_intent = (UPDATE type::record($_intent_table, $_intent_id) SET version = version WHERE version = $_intent_version AND state = 'Preparing' RETURN AFTER)")
                .WithParam("_intent_table", NftLifecycle.SchemaNameConst).WithParam("_intent_id", lifecycle.Id).WithParam("_intent_version", lifecycle.Version),
            SurrealQuery.Of("IF array::len($_intent) != 1 { THROW 'NFT_RESERVATION_CONFLICT' }"),
            SurrealQuery.Of("LET $_reserved = (UPDATE type::record($_asset_table, $_asset_id) SET active_lifecycle_id = $_intent_id WHERE version = $_asset_version AND burned = false AND (active_lifecycle_id = NONE OR active_lifecycle_id = $_intent_id) RETURN AFTER)")
                .WithParam("_asset_table", NftAssetAuthority.SchemaNameConst).WithParam("_asset_id", authority.Id)
                .WithParam("_asset_version", authority.Version).WithParam("_intent_id", lifecycle.Id),
            SurrealQuery.Of("IF array::len($_reserved) != 1 { THROW 'NFT_RESERVATION_CONFLICT' }"),
            SurrealQuery.Of("COMMIT"));
        try
        {
            await ExecuteRetryingAsync(query, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("NFT_RESERVATION_CONFLICT", StringComparison.Ordinal))
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<NftLifecycle?> ClaimAsync(NftLifecycle candidate, CancellationToken ct = default)
    {
        var existing = await GetAsync(candidate.Id, ct);
        if (existing is not null) return existing;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(candidate.RequestJson)
            ?? throw new InvalidOperationException("NFT intent is unreadable.");
        if (!NftProtocolCostAdmissionFence.Matches(candidate, command)) return null;
        try
        {
            await ExecuteRetryingAsync(Fenced(SurrealWriter.Create(candidate), candidate, NftLifecycleWrite.FreshAdmission).Query, ct);
            return await GetAsync(candidate.Id, ct) ?? throw new InvalidOperationException("NFT admission disappeared.");
        }
        catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
        {
            return await GetAsync(candidate.Id, ct) ?? throw new InvalidOperationException("NFT admission disappeared.");
        }
        catch (ActionEffectAuthorityChangedException) { return null; }
        catch (SurrealStatementException exception) when (exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            throw new ActionEffectAuthorityChangedException();
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains(NftProtocolCostAdmissionFence.FailureMarker, StringComparison.Ordinal))
        {
            return await GetAsync(candidate.Id, ct);
        }
    }

    /// <inheritdoc/>
    public Task<NftLifecycle?> GetAsync(string id, CancellationToken ct = default)
        => executor.QuerySingleAsync<NftLifecycle>(SurrealQuery<NftLifecycle>.Key(id), ct);

    /// <inheritdoc/>
    public async Task<NftLifecycle?> UpdateAsync(NftLifecycle row, long expectedVersion, CancellationToken ct = default, bool requireActionAuthority = false)
    {
        var before = await GetAsync(row.Id, ct);
        if (before is null || before.Version != expectedVersion) return null;
        var consentAdded = before.StageConsentsJson != row.StageConsentsJson;
        var participantRenewal = consentAdded && NftStageConsentIdentity.CanRenew(before, row);
        if (consentAdded && (!requireActionAuthority || !(participantRenewal || NftStageConsentIdentity.CanAppend(before, row)))) return null;
        var costsAdded = before.ReplacementCostConsentsJson != row.ReplacementCostConsentsJson;
        if (costsAdded && (!requireActionAuthority || !AZOA.WebAPI.Helpers.NftReplacementCostContract.CanAppend(before, row))) return null;
        var costsRetired = before.ReplacementCostRetirementsJson != row.ReplacementCostRetirementsJson;
        if (costsRetired && !AZOA.WebAPI.Helpers.NftReplacementCostContract.CanRetire(before, row)) return null;
        var renewed = before.ActorContinuationsJson != row.ActorContinuationsJson;
        if (renewed && (!requireActionAuthority || !NftActorContinuationIdentity.CanAppend(before, row))) return null;
        // A renewal-only write re-checks just the new credential; signing steps later fence every authority again.
        NftLifecycleWrite? write = !requireActionAuthority ? null : renewed ? NftLifecycleWrite.RequesterRenewal
            : participantRenewal ? NftLifecycleWrite.ParticipantRenewal : costsAdded ? NftLifecycleWrite.CostAcceptance : NftLifecycleWrite.Effect;
        var update = SurrealWriter.UpdateOnly<NftLifecycle>(row.Id).Where(item => item.Version == expectedVersion
            && item.RequestHash == row.RequestHash && item.RequestJson == row.RequestJson)
            .Set(item => item.State, row.State).Set(item => item.Stage, row.Stage)
            .Set(item => item.ConfirmedStageCount, row.ConfirmedStageCount)
            .Set(item => item.TransactionHistoryJson, row.TransactionHistoryJson)
            .Set(item => item.StageConsentsJson, row.StageConsentsJson)
            .Set(item => item.ReplacementCostConsentsJson, row.ReplacementCostConsentsJson)
            .Set(item => item.ReplacementCostRetirementsJson, row.ReplacementCostRetirementsJson)
            .Set(item => item.ActorContinuationsJson, row.ActorContinuationsJson)
            .Set(item => item.PreparationGeneration, row.PreparationGeneration)
            .Set(item => item.Version, expectedVersion + 1).Set(item => item.UpdatedAt, row.UpdatedAt);
        update = row.PreparedTransactionJson is null ? update.Unset(item => item.PreparedTransactionJson) : update.Set(item => item.PreparedTransactionJson, row.PreparedTransactionJson);
        update = row.AssetId is null ? update.Unset(item => item.AssetId) : update.Set(item => item.AssetId, row.AssetId);
        update = row.ProtocolIdentity is null ? update.Unset(item => item.ProtocolIdentity) : update.Set(item => item.ProtocolIdentity, row.ProtocolIdentity);
        update = row.ContractAddress is null ? update.Unset(item => item.ContractAddress) : update.Set(item => item.ContractAddress, row.ContractAddress);
        update = row.TokenStandard is null ? update.Unset(item => item.TokenStandard) : update.Set(item => item.TokenStandard, row.TokenStandard);
        update = row.CreationTransactionId is null ? update.Unset(item => item.CreationTransactionId) : update.Set(item => item.CreationTransactionId, row.CreationTransactionId);
        update = row.FinalTransactionId is null ? update.Unset(item => item.FinalTransactionId) : update.Set(item => item.FinalTransactionId, row.FinalTransactionId);
        update = row.Detail is null ? update.Unset(item => item.Detail) : update.Set(item => item.Detail, row.Detail);
        try
        {
            var fenced = Fenced(update.Build(), row, write, costsRetired);
            var response = await ExecuteRetryingAsync(fenced.Query, ct);
            return response.GetValues<NftLifecycle>(fenced.ResultIndex).SingleOrDefault();
        }
        catch (ActionEffectAuthorityChangedException) { return null; }
        catch (SurrealStatementException exception) when (exception.Message.Contains(ActionExecutionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(NftProtocolCostAdmissionFence.FailureMarker, StringComparison.Ordinal)
            || exception.Message.Contains(ParticipantConsentLive, StringComparison.Ordinal))
        {
            return null;
        }
    }

    private const string ParticipantConsentLive = "NFT_PARTICIPANT_CONSENT_LIVE";

    // raw: advisory reads of the superseded participant credential; renewal is allowed only once it lapsed or was revoked,
    // and the renewing credential itself is fenced in the same batch. See AGENTS.md §NFT effect authorities.
    private static IReadOnlyList<SurrealQuery> SupersededConsentLapsed(SigningCredentialWitness held) =>
    [
        SurrealQuery.Of("LET $_nft_held_user = (SELECT is_active, auth_not_before FROM ONLY type::record('user', $_nft_held_actor))")
            .WithParam("_nft_held_actor", held.ActorUserId.ToString("N")),
        SurrealQuery.Of("LET $_nft_held_key = (SELECT is_active, revoked_at, expires_at FROM ONLY type::record('api_key', $_nft_held_key_id))")
            .WithParam("_nft_held_key_id", held.ApiKeyId?.ToString("N") ?? "none"),
        SurrealQuery.Of("""
            IF $_nft_held_user != NONE AND $_nft_held_user.is_active = true
                AND (($_nft_held_watermark_present AND $_nft_held_user.auth_not_before = $_nft_held_watermark)
                    OR (!$_nft_held_watermark_present AND $_nft_held_user.auth_not_before = NONE))
                AND (!$_nft_held_expires_present OR $_nft_held_expires > time::now())
                AND (!$_nft_held_is_key OR ($_nft_held_key != NONE AND $_nft_held_key.is_active = true AND $_nft_held_key.revoked_at = NONE
                    AND ($_nft_held_key.expires_at = NONE OR $_nft_held_key.expires_at > time::now())))
            { THROW 'NFT_PARTICIPANT_CONSENT_LIVE' }
            """).WithParam("_nft_held_watermark_present", held.AuthNotBefore.HasValue)
            .WithParam("_nft_held_watermark", held.AuthNotBefore ?? DateTimeOffset.UnixEpoch)
            .WithParam("_nft_held_expires_present", held.ExpiresAt.HasValue)
            .WithParam("_nft_held_expires", held.ExpiresAt ?? DateTimeOffset.UnixEpoch)
            .WithParam("_nft_held_is_key", held.Kind == "ApiKey"),
    ];

    // raw: workflow authority and NFT admission or new-signature/egress authorization commit atomically; see AGENTS.md.
    private static (SurrealQuery Query, int ResultIndex) Fenced(SurrealQuery mutation, NftLifecycle row, NftLifecycleWrite? write,
        bool replacementCostRetirement = false)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)
            ?? throw new InvalidOperationException("NFT intent is unreadable.");
        var guards = new List<SurrealQuery>();
        if (replacementCostRetirement)
        {
            var retired = NftReplacementCostContract.Retirements(row).Last();
            guards.AddRange(SigningAuthorityFence.Create(retired.Authority, [AzoaScopes.NftMint], "nft_cost_retirement")
                ?? throw new ActionEffectAuthorityChangedException());
        }
        if (write is { } kind)
        {
            if (kind == NftLifecycleWrite.ParticipantRenewal)
                guards.AddRange(SupersededConsentLapsed((NftStageConsentIdentity.Superseded(row)
                    ?? throw new ActionEffectAuthorityChangedException()).Authority.Credential));
            var requirements = NftEffectAuthorities.Select(row, command, kind) ?? throw new ActionEffectAuthorityChangedException();
            foreach (var requirement in requirements)
                guards.AddRange(SigningAuthorityFence.Create(requirement.Authority, [requirement.Scope], requirement.Prefix)
                    ?? throw new ActionEffectAuthorityChangedException());
            if (kind == NftLifecycleWrite.FreshAdmission)
            {
                if (row.StageConsentsJson != "[]" || row.ReplacementCostConsentsJson != "[]" || row.ReplacementCostRetirementsJson != "[]"
                    || row.ActorContinuationsJson != "[]") throw new ActionEffectAuthorityChangedException();
                if (command.ProtocolCostQuote is { } quote) guards.AddRange(NftProtocolCostAdmissionFence.Create(quote));
            }
            if (kind == NftLifecycleWrite.CostAcceptance)
                guards.AddRange(NftProtocolCostAdmissionFence.Create((NftReplacementCostContract.Current(row)
                    ?? throw new ActionEffectAuthorityChangedException()).Quote));
            if (kind is NftLifecycleWrite.FreshAdmission or NftLifecycleWrite.Effect or NftLifecycleWrite.CostAcceptance
                && command.ActionAuthority is { } authority)
            {
                if (command.ProtocolCostQuote is not null || command.WorkflowCostAcceptance is not null)
                    guards.AddRange(NftWorkflowCostAuthorityFence.Create(row, command));
                if (command.Actor != authority.ActorUserId || command.ActingTenantId != (authority.ActingTenantId ?? Guid.Empty)
                    || row.ActorUserId != authority.ActorUserId.ToString("N")) throw new InvalidOperationException("NFT action authority conflicts with its parties.");
                guards.AddRange(ActionExecutionFence.Create(authority.RunId.ToString("N"), authority.NodeId.ToString("N"),
                    authority.ActorUserId.ToString("N"), authority.ActingTenantId?.ToString("N"), authority.IntentHash));
            }
        }
        if (guards.Count == 0) return (mutation, 0);
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(SurrealGeneratedStatementScope.Assign(mutation, "nft_effect", "_nft_effect_row"));
        var resultIndex = statements.Count;
        statements.Add(SurrealQuery.Of("RETURN $_nft_effect_row"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        return (SurrealQuery.Combine(statements.ToArray()), resultIndex);
    }

    /// <summary>Retries only engine transaction conflicts; a retried batch loses cleanly on its own version predicates.</summary>
    private Task<SurrealResponse> ExecuteRetryingAsync(SurrealQuery query, CancellationToken ct)
        => SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return response;
        }, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<NftLifecycle>> ListPendingAsync(int limit, CancellationToken ct = default)
        => executor.QueryAsync<NftLifecycle>(SurrealQuery<NftLifecycle>.From()
            .Where(row => row.State == "Submitted" || row.State == "NeedsOperator" || row.State == "Confirmed")
            .OrderBy(row => row.UpdatedAt).Limit(Math.Clamp(limit, 1, 100)), ct);

    /// <inheritdoc/>
    public Task<NftAssetAuthority?> GetAuthorityAsync(string nftId, CancellationToken ct = default)
        => executor.QuerySingleAsync<NftAssetAuthority>(SurrealQuery<NftAssetAuthority>.From().Where(row => row.NftId == nftId).Limit(1), ct);

    /// <inheritdoc/>
    public async Task<bool> CommitAsync(NftLifecycle expected, NftAssetAuthority authority, NftOwnership ownership, CancellationToken ct = default)
    {
        // raw: confirmed lifecycle CAS, unique asset authority, and ownership projection commit together; see AGENTS.md.
        var guard = SurrealQuery.Of("LET $_won = (UPDATE type::record($_lifecycle_table, $_lifecycle_id) SET state = 'Completed', version = version + 1 WHERE version = $_expected AND state = 'Confirmed' RETURN AFTER)")
            .WithParam("_lifecycle_table", NftLifecycle.SchemaNameConst).WithParam("_lifecycle_id", expected.Id).WithParam("_expected", expected.Version);
        var authorityGuard = SurrealQuery.Of("LET $_asset = (UPDATE type::record($_authority_table, $_authority_id) SET version = version WHERE version = $_authority_version AND burned = false AND active_lifecycle_id = $_lifecycle_id RETURN AFTER)")
            .WithParam("_authority_table", NftAssetAuthority.SchemaNameConst).WithParam("_authority_id", authority.Id)
            .WithParam("_authority_version", authority.Version - 1).WithParam("_lifecycle_id", expected.Id);
        var statements = new List<SurrealQuery>
        {
            SurrealQuery.Of("BEGIN"), guard,
            SurrealQuery.Of("IF array::len($_won) != 1 { THROW 'NFT_LIFECYCLE_CHANGED' }"),
        };
        if (expected.Operation != "Mint")
        {
            statements.Add(authorityGuard);
            statements.Add(SurrealQuery.Of("IF array::len($_asset) != 1 { THROW 'NFT_LIFECYCLE_CHANGED' }"));
        }
        statements.Add(SurrealGeneratedStatementScope.Apply(expected.Operation == "Mint"
            ? SurrealWriter.Create(authority) : SurrealWriter.Upsert(authority), "authority"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Upsert(ownership), "ownership"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<NftAssetAuthority>(authority.Id)
            .Unset(row => row.ActiveLifecycleId).Build(), "release"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        var query = SurrealQuery.Combine(statements.ToArray());
        try
        {
            await ExecuteRetryingAsync(query, ct);
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("NFT_LIFECYCLE_CHANGED", StringComparison.Ordinal))
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CancelUnsentAsync(NftLifecycle expected, NftAssetAuthority? authority, SigningConsentWitness consent, CancellationToken ct = default)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(expected.RequestJson)
            ?? throw new InvalidOperationException("NFT intent is unreadable.");
        if (consent.Credential.ActorUserId.ToString("N") != expected.ActorUserId || command.Actor != consent.Credential.ActorUserId
            || (consent.Credential.TenantId ?? Guid.Empty) != command.ActingTenantId) return false;
        var fence = SigningAuthorityFence.ForConsent(consent, [NftSigningScope.ForOperation(expected.Operation)], "nft_cancel");
        if (fence is null) return false;
        // raw: current requester consent, unsent cancellation and its asset reservation release commit together; see AGENTS.md.
        var guard = SurrealQuery.Of("LET $_cancelled = (UPDATE type::record($_cancel_table, $_cancel_id) SET state = 'Cancelled', version = version + 1 WHERE version = $_cancel_version AND state IN ['Preparing', 'Prepared', 'AwaitingRecipientAcceptance', 'AwaitingRefundAuthorization', 'AwaitingRefundDestinationAcceptance'] AND confirmed_stage_count = 0 RETURN AFTER)")
            .WithParam("_cancel_table", NftLifecycle.SchemaNameConst).WithParam("_cancel_id", expected.Id)
            .WithParam("_cancel_version", expected.Version);
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fence);
        statements.Add(guard);
        statements.Add(SurrealQuery.Of("IF array::len($_cancelled) != 1 { THROW 'NFT_LIFECYCLE_CHANGED' }"));
        if (authority is not null)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.UpdateOnly<NftAssetAuthority>(authority.Id)
                .Where(row => row.ActiveLifecycleId == expected.Id).Unset(row => row.ActiveLifecycleId).Build(), "cancel_release"));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            await ExecuteRetryingAsync(SurrealQuery.Combine(statements.ToArray()), ct);
            return true;
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("NFT_LIFECYCLE_CHANGED", StringComparison.Ordinal)
            || exception.Message.Contains(SigningAuthorityFence.FailureMarker, StringComparison.Ordinal))
        {
            return false;
        }
    }
}
