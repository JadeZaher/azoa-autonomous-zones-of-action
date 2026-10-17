using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Governance;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

public sealed partial class SurrealFungibleCreationStore
{
    /// <inheritdoc/>
    public async Task<FungibleAttachmentView?> ReplayAttachmentAsync(string id, FungibleAttachmentRequest request,
        Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var receipt = await executor.QuerySingleAsync<FungibleCreationAttachment>(
            SurrealQuery<FungibleCreationAttachment>.Key(AttachmentReceiptId(id, request, actor.ToString("N"), tenant?.ToString("N"))), ct);
        if (receipt is null || receipt.ActorId != actor.ToString("N") || receipt.TenantId != tenant?.ToString("N")
            || receipt.RequestHash != AttachmentRequestHash(id, request, actor.ToString("N"), tenant?.ToString("N"))) return null;
        var row = await GetAsync(id, ct);
        var projection = await executor.QuerySingleAsync<FungibleCreationProjection>(SurrealQuery<FungibleCreationProjection>.Key(id), ct);
        return row is null || projection is null ? null : await AttachmentViewAsync(row, projection, ct);
    }

    /// <inheritdoc/>
    public async Task<FungibleAttachmentView?> GetAttachmentAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await GetAsync(id, ct);
        var projection = await executor.QuerySingleAsync<FungibleCreationProjection>(SurrealQuery<FungibleCreationProjection>.Key(id), ct);
        if (row is null || row.TenantId != tenant?.ToString("N") || projection?.HolonId is null) return null;
        var holon = await executor.QuerySingleAsync<SurrealHolonStore.HolonPoco>(SurrealQuery<SurrealHolonStore.HolonPoco>.Key(projection.HolonId), ct);
        if (holon is not { IsActive: true } || holon.UserId != SurrealLink.ToLink("user", actor.ToString("N"))) return null;
        return await AttachmentViewAsync(row, projection, ct);
    }

    /// <inheritdoc/>
    public async Task<FungibleAttachmentView?> AttachAsync(string id, FungibleAttachmentRequest request,
        SigningConsentWitness consent, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.Version < 0 || string.IsNullOrWhiteSpace(request.ProjectionAuthorityId)) return null;
        var actor = consent.Credential.ActorUserId.ToString("N");
        var tenant = consent.Credential.TenantId?.ToString("N");
        var receiptId = AttachmentReceiptId(id, request, actor, tenant);
        var requestHash = AttachmentRequestHash(id, request, actor, tenant);
        var prior = await executor.QuerySingleAsync<FungibleCreationAttachment>(SurrealQuery<FungibleCreationAttachment>.Key(receiptId), ct);
        var row = await GetAsync(id, ct);
        var projection = await executor.QuerySingleAsync<FungibleCreationProjection>(SurrealQuery<FungibleCreationProjection>.Key(id), ct);
        if (row is null || row.TenantId != tenant || projection is null) return null;
        if (prior is not null)
            return prior.RequestHash == requestHash && prior.ActorId == actor && prior.TenantId == tenant
                ? await AttachmentViewAsync(row, projection, ct) : null;
        var view = await AttachmentViewAsync(row, projection, ct);
        if (view is null || row.State != "AttachmentPending" || row.Version != request.Version
            || projection.AuthorityId != request.ProjectionAuthorityId || projection.InitialAttachmentState != "Pending") return null;
        var fences = SigningAuthorityFence.ForConsent(consent, [AzoaScopes.DappDevelop], "fungible_attachment");
        if (fences is null) return null;
        var receipt = new FungibleCreationAttachment
        {
            Id = receiptId, OperationId = id, ProjectionAuthorityId = projection.AuthorityId, HolonId = projection.HolonId!,
            ActorId = actor, TenantId = tenant, ExpectedVersion = request.Version, RequestHash = requestHash,
            ConsentJson = CanonicalJson.Canonicalize(System.Text.Json.JsonSerializer.SerializeToElement(consent)), CreatedAt = DateTimeOffset.UtcNow,
        };
        // raw: current owner consent, exact finalized association, operation CAS and unique receipt commit together.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(fences);
        statements.Add(LocalEntityMutationFence.Holons());
        var ownerLink = SurrealLink.ToLink("user", actor);
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<SurrealHolonStore.HolonPoco>(projection.HolonId!)
            .Where(holon => holon.IsActive && holon.UserId == ownerLink && (holon.TokenId == null || holon.TokenId == "")
                && (holon.ChainId == null || holon.ChainId == "" || holon.ChainId == view.ChainType)
                && (holon.AssetType == null || holon.AssetType != ConsumerGovernanceHolonTypes.ProposalAssetType))
            .Set(holon => holon.TokenId, view.AssetId).Set(holon => holon.ChainId, view.ChainType)
            .Set(holon => holon.ModifiedDate, DateTimeOffset.UtcNow).Build(), "fungible_attachment_holon", "_fungible_attachment_holon"));
        statements.Add(SurrealQuery.Of("IF $_fungible_attachment_holon = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Assign(SurrealWriter.UpdateOnly<FungibleCreationOperation>(id)
            .Where(current => current.Version == row.Version && current.State == "AttachmentPending" && current.RequestHash == row.RequestHash
                && current.AssetId == view.AssetId && current.ObservationJson == row.ObservationJson)
            .Set(current => current.State, "Completed").Set(current => current.Version, row.Version + 1)
            .Unset(current => current.Detail).Set(current => current.UpdatedAt, DateTimeOffset.UtcNow).Build(),
            "fungible_attachment_operation", "_fungible_attachment_operation"));
        statements.Add(SurrealQuery.Of("IF $_fungible_attachment_operation = NONE { THROW 'FUNGIBLE_CREATION_CHANGED' }"));
        statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(receipt), "fungible_attachment_receipt"));
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
            return view with { State = "Completed", Version = row.Version + 1 };
        }
        catch (SurrealStatementException exception) when (IsConflict(exception))
        {
            prior = await executor.QuerySingleAsync<FungibleCreationAttachment>(SurrealQuery<FungibleCreationAttachment>.Key(receiptId), ct);
            var current = await GetAsync(id, ct);
            return prior?.RequestHash == requestHash && current is not null ? await AttachmentViewAsync(current, projection, ct) : null;
        }
    }

    private async Task<FungibleAttachmentView?> AttachmentViewAsync(FungibleCreationOperation row, FungibleCreationProjection projection, CancellationToken ct)
    {
        if (row.State is not ("AttachmentPending" or "Completed") || projection.Id != row.Id || projection.RequestHash != row.RequestHash
            || !Guid.TryParse(projection.HolonId, out var holonId) || Command(row).HolonId != holonId) return null;
        var authority = await executor.QuerySingleAsync<FungibleAssetAuthority>(SurrealQuery<FungibleAssetAuthority>.Key(projection.AuthorityId), ct);
        if (authority is null || authority.OperationId != row.Id || authority.AssetId != row.AssetId || authority.IntentJson != row.IntentJson) return null;
        return new(row.Id, row.Version, row.State, holonId, authority.Id, authority.ChainType, authority.Network, authority.AssetId, authority.CreationTransactionId);
    }

    private static string AttachmentReceiptId(string id, FungibleAttachmentRequest request, string actor, string? tenant)
        => CanonicalJson.Hash(new { Domain = "fungible-attachment-v1", Operation = id, Actor = actor, Tenant = tenant, request.RequestId });

    private static string AttachmentRequestHash(string id, FungibleAttachmentRequest request, string actor, string? tenant)
        => CanonicalJson.Hash(new { Operation = id, Actor = actor, Tenant = tenant, Request = request });
}
