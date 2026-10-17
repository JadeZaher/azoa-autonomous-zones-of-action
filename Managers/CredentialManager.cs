using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain;

namespace AZOA.WebAPI.Managers;

/// <inheritdoc/>
public sealed class CredentialManager(INftLifecycleStore store, INftLifecycleService lifecycle,
    IBlockchainProviderFactory providers) : ICredentialManager
{
    public const string OperationTypeName = "CredentialRevoke";

    /// <inheritdoc/>
    public async Task<AZOAResult<CredentialRevokeResult>> RevokeAsync(string assetId, CredentialRevokeRequest request,
        Guid callerUserId, string? clientIdempotencyKey, Guid? actingTenantId = null, CancellationToken ct = default)
    {
        if (request is null || callerUserId == Guid.Empty || string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(request.ChainType))
            return AZOAResult<CredentialRevokeResult>.Failure("Caller, chain, and asset identity are required.");
        ChainNetwork? requestedNetwork = null;
        if (request.Network is not null)
        {
            if (!Enum.TryParse<ChainNetwork>(request.Network, true, out var parsed) || !Enum.IsDefined(parsed))
                return AZOAResult<CredentialRevokeResult>.Failure("Credential network is invalid.");
            requestedNetwork = parsed;
        }
        var key = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? NftLifecycleIdentity.Fingerprint(JsonSerializer.Serialize(new[] { request.ChainType.ToUpperInvariant(), requestedNetwork?.ToString(), assetId, request.ContractAddress, request.Reason }))
            : clientIdempotencyKey;
        var id = NftLifecycleIdentity.ForRequest(callerUserId, "Revoke", key, actingTenantId ?? Guid.Empty);
        var replay = await store.GetAsync(id, ct);
        Guid nftId;
        if (replay is not null)
        {
            if (replay.State == "Cancelled")
                return AZOAResult<CredentialRevokeResult>.FailureWithCode("This revocation request was cancelled; use a new key for a new request.", "NFT_CANCELLED");
            if (replay.AssetId != assetId || !string.Equals(replay.ChainType, request.ChainType, StringComparison.OrdinalIgnoreCase)
                || (requestedNetwork is not null && replay.Network != requestedNetwork.Value.ToString())
                || (request.ContractAddress is not null && request.ContractAddress != replay.ContractAddress))
                return AZOAResult<CredentialRevokeResult>.FailureWithCode("Idempotency-Key belongs to another credential.", AzoaErrorCodes.Conflict);
            nftId = Guid.Parse(replay.NftId);
        }
        else
        {
            var provider = providers.GetProvider(request.ChainType, requestedNetwork ?? providers.DefaultNetwork);
            var credential = await store.FindCredentialAsync(provider.ChainType, provider.ActiveNetwork.ToString(), assetId, request.ContractAddress, ct);
            if (credential is not { HasVerifiedIssuance: true } || credential.IssuerUserId != callerUserId
                || !credential.IsSoulbound || credential.HolderUserId == callerUserId)
                return AZOAResult<CredentialRevokeResult>.FailureWithCode("Confirmed issuance does not authorize this caller to revoke the credential.", AzoaErrorCodes.Forbidden);
            nftId = credential.Id;
        }
        var result = request.AdmissionOnly
            ? await lifecycle.AdmitRevokeAsync(callerUserId, nftId, key, ct, actingTenantId ?? Guid.Empty, request.FeeAuthorization, request.Reason, request.ActionAuthority)
            : await lifecycle.RevokeAsync(callerUserId, nftId, key, ct, actingTenantId ?? Guid.Empty, request.FeeAuthorization, request.Reason, request.ActionAuthority, request.AuthenticationWitness);
        if (result.IsError || result.Result is null) return AZOAResult<CredentialRevokeResult>.FailureWithCode(result.Message, result.Code ?? AzoaErrorCodes.OperationNotAllowed);
        var row = await store.GetAsync(id, ct) ?? throw new InvalidOperationException("Credential lifecycle disappeared.");
        if (row.State == "Cancelled")
            return AZOAResult<CredentialRevokeResult>.FailureWithCode("This revocation request was cancelled; use a new key for a new request.", "NFT_CANCELLED");
        var intent = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)
            ?? throw new InvalidOperationException("Credential lifecycle intent is unreadable.");
        var prepared = row.PreparedTransactionJson is null ? null : JsonSerializer.Deserialize<PreparedNftTransaction>(row.PreparedTransactionJson);
        var confirmed = row.State == "Completed";
        var outcome = new CredentialRevokeResult
        {
            AssetId = assetId, CredentialId = nftId, IssuerUserId = callerUserId,
            HolderUserId = intent.SourceUserId ?? throw new InvalidOperationException("Credential source identity is missing."),
            HolderAddress = intent.SourceAddress ?? throw new InvalidOperationException("Credential source address is missing."),
            RecipientAddress = intent.RecipientAddress, ChainType = row.ChainType,
            OperationId = Guid.Parse(row.Id), LifecycleId = row.Id, LifecycleState = row.State,
            Status = confirmed ? OperationStatus.Completed : OperationStatus.PendingConfirmation,
            TxHash = confirmed ? row.FinalTransactionId : prepared?.Intent.Kind == NftTransactionKinds.Revoke ? prepared.TransactionId : null,
            Replayed = replay is not null,
        };
        return confirmed ? AZOAResult<CredentialRevokeResult>.Success(outcome)
            : AZOAResult<CredentialRevokeResult>.FailureWithCode($"Credential lifecycle {row.Id} is {row.State}; continue the same lifecycle.",
                CredentialRevokeCodes.PendingConfirmation, outcome);
    }
}
