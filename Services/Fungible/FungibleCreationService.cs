using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Governance;
using AZOA.WebAPI.Services.Reserve;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Core;
using SurrealForge.Client;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Services.Fungible;

/// <inheritdoc/>
public sealed class FungibleCreationService(IFungibleCreationStore store, IBlockchainProviderFactory providers,
    IValueAccessService valueAccess, INodeGovernanceGuard governance, ISurrealExecutor executor,
    SigningAdmissionAuthorityFactory signingAuthority) : IFungibleCreationService
{
    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationView>> ReplaceAsync(string id, FungibleContinuationRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var row = await store.GetAsync(id, ct);
        if (!Matches(row, credential.ActorUserId, credential.TenantId) || request.RequestId == Guid.Empty || request.Version < 0)
            return AZOAResult<FungibleCreationView>.Failure("The original creation and exact replacement request are required.");
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row!.RequestJson)!;
        var bound = await BindCurrentAuthorityAsync(command, credential, ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<FungibleCreationView>.Failure(bound.Message);
        if (row.Generation == int.MaxValue) return AZOAResult<FungibleCreationView>.Failure("The generation counter cannot advance.");
        var nextIntent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)! with { Generation = row.Generation + 1, Quote = null };
        var audit = new FungibleCreationAuthorization
        {
            Id = CanonicalJson.Hash(new { Domain = "fungible-replacement-v1", id, credential.ActorUserId, credential.TenantId, request.RequestId }),
            OperationId = id, ActorId = row.ActorUserId, TenantId = row.TenantId, ExpectedVersion = request.Version,
            RequestHash = CanonicalJson.Hash(new { id, credential.ActorUserId, credential.TenantId, request.Version, row.RequestHash }),
            IntentHash = FungibleCreationIdentity.IntentHash(nextIntent), AuthorityJson = bound.Result, CreatedAt = DateTimeOffset.UtcNow,
        };
        var replaced = await store.ReplaceAsync(row, audit, ct);
        return replaced is null ? AZOAResult<FungibleCreationView>.Failure("Replacement requires the unchanged exact no-effect generation and current signing authority.") : Result(replaced);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationView>> CancelAsync(string id, FungibleContinuationRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        var cancelled = await store.CancelAsync(id, request, credential, ct);
        return cancelled is null ? AZOAResult<FungibleCreationView>.Failure("Cancellation requires the unchanged original request and a generation with no authorized chain effect.") : Result(cancelled);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleAttachmentView>> GetAttachmentAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var view = await store.GetAttachmentAsync(id, actor, tenant, ct);
        return view is null ? AZOAResult<FungibleAttachmentView>.Failure("The finalized attachment was not found for this Holon owner.")
            : AZOAResult<FungibleAttachmentView>.Success(view);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleAttachmentView>> AttachAsync(string id, FungibleAttachmentRequest request,
        SigningCredentialWitness credential, CancellationToken ct = default)
    {
        if (request.RequestId == Guid.Empty || request.Version < 0 || string.IsNullOrWhiteSpace(request.ProjectionAuthorityId))
            return AZOAResult<FungibleAttachmentView>.Failure("An exact displayed attachment and request key are required.");
        var replay = await store.ReplayAttachmentAsync(id, request, credential.ActorUserId, credential.TenantId, ct);
        if (replay is not null) return AZOAResult<FungibleAttachmentView>.Success(replay);
        var consent = await signingAuthority.BindConsentAsync(credential, [AzoaScopes.DappDevelop], ct);
        if (consent.IsError || consent.Result is null) return AZOAResult<FungibleAttachmentView>.Failure(consent.Message);
        var attached = await store.AttachAsync(id, request, consent.Result, ct);
        return attached is null ? AZOAResult<FungibleAttachmentView>.Failure("The attachment, current owner authority or request version changed.")
            : AZOAResult<FungibleAttachmentView>.Success(attached);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationQuote>> QuoteAsync(FungibleCreationIntent intent, CancellationToken ct = default)
    {
        var gate = await CheckAuthorityAsync(intent, ct);
        if (gate is not null) return AZOAResult<FungibleCreationQuote>.Failure(gate);
        var module = Module(intent);
        return module is null ? AZOAResult<FungibleCreationQuote>.Failure("This provider has no fungible creation capability.")
            : await module.QuoteFungibleCreationAsync(intent, ct);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationView>> AdmitAsync(FungibleCreationCommand command, CancellationToken ct = default)
    {
        if (command.ActorUserId == Guid.Empty || string.IsNullOrWhiteSpace(command.RequestKey) || command.RequestKey.Length > 200
            || command.ActorUserId != command.Intent.ActorUserId || command.TenantId != command.Intent.TenantId
            || (command.GenesisAuthority is null && command.WalletId == Guid.Empty) || (command.Intent.Quote is not null && !FungibleCreationIdentity.IsValidQuote(command.Intent))
            || command.Intent.Generation != 0 || command.MintTarget is not ("PlatformReserve" or "CallerWallet")
            || (command.MintTarget == "PlatformReserve") != command.Intent.SigningContext.IsPlatform
            || FungibleCreationAuthorityFence.Create(command, null, "fungible_admission_validation") is null
            || command.HolonId == Guid.Empty)
            return AZOAResult<FungibleCreationView>.Failure("The immutable creation request or quote authorization is invalid.");
        var id = FungibleCreationIdentity.OperationId(command.ActorUserId, command.TenantId, command.RequestKey);
        if (command.Intent.PreparationKey != id) return AZOAResult<FungibleCreationView>.Failure("Creation preparation belongs to another request key.");
        var prior = await store.GetAsync(id, ct);
        if (prior is not null) return SameAdmission(prior, command) ? Result(prior) : AZOAResult<FungibleCreationView>.Failure("Creation request key belongs to a different intent.");
        var admissionPolicy = await NodeGovernancePolicyFence.ReadVersionAsync(executor, ct);
        var refusal = await CheckAuthorityAsync(command.Intent, ct, command.GenesisAuthority is not null);
        if (refusal is not null) return AZOAResult<FungibleCreationView>.Failure(refusal);
        command = command with { AdmissionPolicyVersion = admissionPolicy };
        var hash = CanonicalJson.Hash(command);
        var now = DateTimeOffset.UtcNow;
        var row = await store.ClaimAsync(new FungibleCreationOperation
        {
            Id = id, ActorUserId = command.ActorUserId.ToString("N"), TenantId = command.TenantId?.ToString("N"),
            RequestHash = hash, RequestJson = JsonSerializer.Serialize(command), IntentJson = JsonSerializer.Serialize(command.Intent),
            State = command.Intent.Quote is null ? "AwaitingConsent" : "Admitted",
            QuoteExpiresAt = command.Intent.Quote?.ExpiresAt ?? now, AcceptedAt = command.Intent.Quote is null ? DateTimeOffset.UnixEpoch : now,
            CreatedAt = now, UpdatedAt = now,
        }, ct);
        return row is null ? AZOAResult<FungibleCreationView>.Failure("Creation quote expired or current authorization changed before admission.")
            : SameAdmission(row, command) ? Result(row) : AZOAResult<FungibleCreationView>.Failure("Creation request key belongs to a different intent.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationView>> OfferQuoteAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await store.GetAsync(id, ct);
        if (!Matches(row, actor, tenant)) return AZOAResult<FungibleCreationView>.Failure("Creation operation was not found.");
        if (row!.State != "AwaitingConsent") return Result(row);
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)!;
        var policy = await NodeGovernancePolicyFence.ReadVersionAsync(executor, ct);
        var refusal = await CheckAuthorityAsync(intent, ct, command.GenesisAuthority is not null);
        if (refusal is not null) return AZOAResult<FungibleCreationView>.Failure(refusal);
        var module = Module(intent);
        if (module is null) return AZOAResult<FungibleCreationView>.Failure("This provider has no fungible creation capability.");
        var quote = await module.QuoteFungibleCreationAsync(intent, ct);
        if (quote.IsError || quote.Result is null) return AZOAResult<FungibleCreationView>.Failure(quote.Message);
        if (command.GenesisAuthority is { } purpose && quote.Result.ProtocolIdentity != purpose.ProtocolIdentity)
            return AZOAResult<FungibleCreationView>.Failure("The genesis provider identity changed from the original admission.");
        var saved = await store.SetQuoteAsync(row, JsonSerializer.Serialize(quote.Result), false, policy, ct);
        return saved is null ? AZOAResult<FungibleCreationView>.Failure("Creation request changed before the quote was saved.") : Result(saved);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationView>> AcceptQuoteAsync(string id, Guid actor, Guid? tenant, long version, string quoteId, CancellationToken ct = default)
    {
        var row = await store.GetAsync(id, ct);
        if (!Matches(row, actor, tenant)) return AZOAResult<FungibleCreationView>.Failure("Creation operation was not found.");
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row!.IntentJson)!;
        if (row.State != "AwaitingConsent") return intent.Quote?.QuoteId == quoteId ? Result(row)
            : AZOAResult<FungibleCreationView>.Failure("This operation accepted a different quote.");
        var offered = row.OfferedQuoteJson is null ? null : JsonSerializer.Deserialize<FungibleCreationQuote>(row.OfferedQuoteJson);
        if (row.Version != version || offered?.QuoteId != quoteId) return AZOAResult<FungibleCreationView>.Failure("The displayed quote or request version changed.");
        var policy = await NodeGovernancePolicyFence.ReadVersionAsync(executor, ct);
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)!;
        var refused = await CheckAuthorityAsync(intent, ct, command.GenesisAuthority is not null);
        if (refused is not null) return AZOAResult<FungibleCreationView>.Failure(refused);
        var saved = await store.SetQuoteAsync(row, row.OfferedQuoteJson!, true, policy, ct);
        return saved is null ? AZOAResult<FungibleCreationView>.Failure("The quote expired or current authority changed before acceptance.") : Result(saved);
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationView>> ContinueAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default,
        SigningCredentialWitness? credential = null, FungibleContinuationRequest? authorization = null)
    {
        var row = await store.GetAsync(id, ct);
        if (!Matches(row, actor, tenant)) return AZOAResult<FungibleCreationView>.Failure("Creation operation was not found.");
        if (row!.State is "Completed" or "AttachmentPending" or "Cancelled" or "Confirmed" or "Submitted" or "NeedsOperator")
            return await RefreshAsync(id, actor, tenant, ct);
        if (credential is null || credential.ActorUserId != actor || credential.TenantId != tenant
            || authorization is null || authorization.RequestId == Guid.Empty || authorization.Version < 0)
            return AZOAResult<FungibleCreationView>.Failure("Fresh participant credentials and an exact operation-version authorization are required.");
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)!;
        var bound = await BindCurrentAuthorityAsync(command, credential, ct);
        if (bound.IsError || bound.Result is null) return AZOAResult<FungibleCreationView>.Failure(bound.Message);
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        var intentHash = FungibleCreationIdentity.IntentHash(intent);
        var audit = new FungibleCreationAuthorization
        {
            Id = CanonicalJson.Hash(new { Domain = "fungible-continuation-v1", id, actor, tenant, authorization.RequestId })[..32],
            OperationId = id, ActorId = actor.ToString("N"), TenantId = tenant?.ToString("N"), ExpectedVersion = authorization.Version,
            RequestHash = CanonicalJson.Hash(new { id, actor, tenant, authorization.Version, IntentHash = intentHash }),
            IntentHash = intentHash, AuthorityJson = bound.Result,
            PreparedHash = row.PreparedJson is null ? null : CanonicalJson.HashCanonical(row.PreparedJson), CreatedAt = DateTimeOffset.UtcNow,
        };
        var saved = await store.AuthorizeAsync(row, audit, ct);
        return saved is null ? AZOAResult<FungibleCreationView>.Failure("Current authority or the preserved creation version changed.")
            : await AdvanceAsync(id, actor, tenant, true, ct);
    }
    /// <inheritdoc/>
    public Task<AZOAResult<FungibleCreationView>> RefreshAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
        => AdvanceAsync(id, actor, tenant, false, ct);
    /// <inheritdoc/>
    public async Task<AZOAResult<FungibleCreationView>> GetAsync(string id, Guid actor, Guid? tenant, CancellationToken ct = default)
    {
        var row = await store.GetAsync(id, ct);
        return Matches(row, actor, tenant) ? Result(row!) : AZOAResult<FungibleCreationView>.Failure("Creation operation was not found.");
    }
    /// <inheritdoc/>
    public async Task<IReadOnlyList<FungibleCreationView>> ListAsync(Guid actor, Guid? tenant, int offset, int limit, CancellationToken ct = default)
        => (await store.ListParticipantAsync(actor, tenant, offset, limit, ct)).Select(View).ToArray();

    private async Task<AZOAResult<FungibleCreationView>> AdvanceAsync(string id, Guid actor, Guid? tenant, bool interactive, CancellationToken ct)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var row = await store.GetAsync(id, ct);
            if (!Matches(row, actor, tenant)) return AZOAResult<FungibleCreationView>.Failure("Creation operation was not found.");
            if (row!.State is "Completed" or "AttachmentPending" or "Cancelled" or "FailedNoEffect" or "AwaitingConsent") return Result(row);
            var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)!;
            var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
            if (row.State == "Confirmed")
            {
                var observed = JsonSerializer.Deserialize<FungibleCreationObservation>(row.ObservationJson!)!;
                var authority = new FungibleAssetAuthority
                {
                    Id = CanonicalJson.Hash(new { intent.ChainType, intent.Network, observed.ProtocolIdentity, observed.AssetId }),
                    OperationId = row.Id, IssuerUserId = row.ActorUserId, TenantId = row.TenantId,
                    ChainType = intent.ChainType, Network = intent.Network.ToString(), ProtocolIdentity = observed.ProtocolIdentity,
                    AssetId = observed.AssetId!, CreatorAddress = intent.CreatorAddress, CreationTransactionId = observed.TransactionId,
                    IntentHash = CanonicalJson.Hash(intent), IntentJson = row.IntentJson, EvidenceJson = observed.EvidenceJson!, CreatedAt = DateTimeOffset.UtcNow,
                };
                var reserve = command.MintTarget == "PlatformReserve" ? ReserveAssetRegistrationPlan.Create(
                    new ReserveAssetRegistration(intent.ChainType, observed.AssetId!, intent.UnitName, intent.Decimals,
                        BigInteger.Parse(intent.TotalBaseUnits, System.Globalization.CultureInfo.InvariantCulture), command.TenantId),
                    new ReserveAccount(intent.ChainType, intent.CreatorAddress, true), intent.Network.ToString(), observed.ProtocolIdentity, DateTimeOffset.UtcNow) : null;
                if (await store.CompleteAsync(row, authority, reserve, ct)) continue;
                return Pending(row, "Chain creation is confirmed; reserve or holon projection requires recovery.");
            }
            var module = Module(intent);
            if (module is null) return Pending(row, "The bound creation provider is unavailable.");
            if (row.State is "Admitted" or "Preparing" or "Prepared")
            {
                if (!interactive) return Result(row);
                var policy = await NodeGovernancePolicyFence.ReadVersionAsync(executor, ct);
                var refused = await CheckAuthorityAsync(intent, ct, command.GenesisAuthority is not null);
                if (refused is not null) return Pending(row, refused);
                if (row.State == "Admitted")
                {
                    if (await ChangeAsync(row, "Preparing", true, policy, ct) is null) continue;
                    continue;
                }
                if (row.State == "Preparing")
                {
                    var authorized = await ChangeAsync(row, "Preparing", true, policy, ct);
                    if (authorized is null) continue;
                    row = authorized;
                    var prepared = await module.PrepareFungibleCreationAsync(intent, ct);
                    if (prepared.IsError || prepared.Result is null) return Pending(row, prepared.Message);
                    if (CanonicalJson.Hash(prepared.Result.Intent) != CanonicalJson.Hash(intent)
                        || prepared.Result.PreparationId != FungibleCreationIdentity.PreparationId(intent)
                        || prepared.Result.ProtocolIdentity != intent.Quote!.ProtocolIdentity
                        || string.IsNullOrWhiteSpace(prepared.Result.TransactionId) || string.IsNullOrWhiteSpace(prepared.Result.SignedTransactionBase64))
                        return Pending(row, "Prepared creation differs from the admitted intent.");
                    row.PreparedJson = JsonSerializer.Serialize(prepared.Result);
                    if (await ChangeAsync(row, "Prepared", true, policy, ct) is null) continue;
                    continue;
                }
                if (await ChangeAsync(row, "Submitted", true, policy, ct) is null) continue;
                continue;
            }
            if (row.State is "Submitted" or "NeedsOperator")
            {
                if (row.PreparedJson is null) return Pending(row, "The saved creation candidate requires operator investigation.");
                var prepared = JsonSerializer.Deserialize<PreparedFungibleCreation>(row.PreparedJson)!;
                var observed = await module.ObserveFungibleCreationAsync(prepared, ct);
                if (observed.IsError || observed.Result is null) return Pending(row, observed.Message);
                var proof = observed.Result;
                if (proof.TransactionId != prepared.TransactionId || proof.ProtocolIdentity != prepared.ProtocolIdentity)
                    return Pending(row, "Creation observation conflicts with the original candidate.");
                if (proof.Verdict == "Confirmed")
                {
                    if (string.IsNullOrWhiteSpace(proof.AssetId) || string.IsNullOrWhiteSpace(proof.EvidenceJson))
                        return Pending(row, "Finalized creation evidence is incomplete.");
                    row.AssetId = proof.AssetId;
                    row.ObservationJson = JsonSerializer.Serialize(proof);
                    if (await ChangeAsync(row, "Confirmed", false, null, ct) is null) continue;
                    continue;
                }
                if (proof.Verdict == "ExpiredAbsent")
                {
                    if (string.IsNullOrWhiteSpace(proof.EvidenceJson)) return Pending(row, "Authoritative no-effect history is incomplete.");
                    row.ObservationJson = JsonSerializer.Serialize(proof);
                    if (await ChangeAsync(row, "FailedNoEffect", false, null, ct) is null) continue;
                    continue;
                }
                if (proof.Verdict == "Pending")
                {
                    await module.SubmitFungibleCreationAsync(prepared, ct);
                }
                else if (row.State != "NeedsOperator")
                {
                    if (await ChangeAsync(row, "NeedsOperator", false, null, ct) is null) continue;
                    continue;
                }
                return Result(row);
            }
            return Pending(row, "Creation state requires operator investigation.");
        }
        return await GetAsync(id, actor, tenant, ct);
    }

    private Task<FungibleCreationOperation?> ChangeAsync(FungibleCreationOperation row, string state, bool authorize, long? policy, CancellationToken ct)
    {
        var expectedState = row.State;
        row.State = state;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        return store.UpdateAsync(row, row.Version, expectedState, authorize, policy, ct);
    }
    private async Task<AZOAResult<string>> BindCurrentAuthorityAsync(FungibleCreationCommand command, SigningCredentialWitness credential, CancellationToken ct)
    {
        if (command.GenesisAuthority is { } original)
        {
            var purpose = original with { Credential = credential };
            return PlatformGenesisIdentity.Matches(command, purpose)
                ? AZOAResult<string>.Success(JsonSerializer.Serialize(purpose))
                : AZOAResult<string>.Failure("A current node-operator credential for the original genesis is required.");
        }
        var bound = await signingAuthority.BindDirectAsync(credential, command.WalletId, command.Intent.ChainType,
            command.WalletAddress, [AzoaScopes.TokenCreateSign], ct);
        return bound.IsError || bound.Result is null ? AZOAResult<string>.Failure(bound.Message)
            : AZOAResult<string>.Success(JsonSerializer.Serialize(bound.Result));
    }

    private async Task<string?> CheckAuthorityAsync(FungibleCreationIntent intent, CancellationToken ct, bool platformGenesis = false)
    {
        var allowed = await governance.EnsureAllowedAsync(intent.ChainType, NodeGovernanceAssetTypes.FungibleToken, "fungible-token:create", ct);
        if (allowed.IsRefused) return allowed.ConsumerMessage;
        if (platformGenesis) return null;
        var ready = await valueAccess.RequireValueAccessAsync(intent.ActorUserId, intent.TenantId, ct);
        return ready.IsError ? ready.Message : null;
    }
    private IFungibleCreationModule? Module(FungibleCreationIntent intent)
    {
        try { return providers.GetProvider(intent.ChainType, intent.Network).TryGetModule<IFungibleCreationModule>(out var module) ? module : null; }
        catch (BlockchainProviderNotFoundException) { return null; }
    }
    private static bool Matches(FungibleCreationOperation? row, Guid actor, Guid? tenant)
        => row is not null && row.ActorUserId == actor.ToString("N") && row.TenantId == tenant?.ToString("N");
    private static bool SameAdmission(FungibleCreationOperation row, FungibleCreationCommand command)
        => JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson) is { } original
            && CanonicalJson.Hash(original) == row.RequestHash
            && CanonicalJson.Hash(original with { WalletProvisioned = false, AdmissionPolicyVersion = null })
                == CanonicalJson.Hash(command with { WalletProvisioned = false, AdmissionPolicyVersion = null });
    private static AZOAResult<FungibleCreationView> Result(FungibleCreationOperation row) => AZOAResult<FungibleCreationView>.Success(View(row));
    private static AZOAResult<FungibleCreationView> Pending(FungibleCreationOperation row, string detail)
        => AZOAResult<FungibleCreationView>.Success(View(row) with { Detail = detail });
    private static FungibleCreationView View(FungibleCreationOperation row)
    {
        var command = JsonSerializer.Deserialize<FungibleCreationCommand>(row.RequestJson)!;
        var intent = JsonSerializer.Deserialize<FungibleCreationIntent>(row.IntentJson)!;
        var prepared = row.PreparedJson is null ? null : JsonSerializer.Deserialize<PreparedFungibleCreation>(row.PreparedJson);
        return new(row.Id, row.Version, row.State == "Confirmed" ? "ProjectionPending" : row.State, command.Intent.ChainType, command.Intent.Network.ToString(),
            row.State is "Completed" or "AttachmentPending" ? row.AssetId : null, prepared?.TransactionId, row.Detail,
            intent.Quote ?? (row.OfferedQuoteJson is null ? null : JsonSerializer.Deserialize<FungibleCreationQuote>(row.OfferedQuoteJson)),
            command.WalletId, command.WalletAddress, command.WalletProvisioned, command.HolonId,
            command.GenesisAuthority is null ? "Participant" : "PlatformGenesis");
    }
}
