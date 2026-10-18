using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.TestSupport;

internal sealed class InMemoryNftLifecycleStore : INftLifecycleStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, NftLifecycle> _rows = new();
    private readonly Dictionary<string, NftAssetAuthority> _assets = new();
    internal Dictionary<string, NftOwnership> Ownership { get; } = new();
    internal Func<NftLifecycle, Task>? BeforeClaim { get; set; }
    internal Func<NftLifecycle, Task>? BeforeUpdate { get; set; }
    /// <summary>Clock for the mirrored direct-credential expiry the durable transaction fence enforces.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<NftLifecycle?> ClaimAsync(NftLifecycle candidate, CancellationToken ct = default)
    {
        if (BeforeClaim is { } pause) await pause(candidate);
        lock (_sync)
        {
            if (!_rows.ContainsKey(candidate.Id))
            {
                if (!Authorized(candidate, NftLifecycleWrite.FreshAdmission)) return null;
                _rows.Add(candidate.Id, Copy(candidate));
            }
            return Copy(_rows[candidate.Id]);
        }
    }
    public Task<NftLifecycle?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult(_rows.TryGetValue(id, out var row) ? Copy(row) : null);
    }
    public async Task<NftLifecycle?> UpdateAsync(NftLifecycle row, long expectedVersion, CancellationToken ct = default, bool requireActionAuthority = false,
        bool workflowAuthority = false)
    {
        if (BeforeUpdate is { } pause) await pause(row);
        lock (_sync)
        {
            if (!_rows.TryGetValue(row.Id, out var existing) || existing.Version != expectedVersion
                || existing.RequestHash != row.RequestHash || existing.RequestJson != row.RequestJson) return null;
            if (workflowAuthority && NftActorContinuationIdentity.TakenOver(existing,
                JsonSerializer.Deserialize<AZOA.WebAPI.Models.Blockchain.NftLifecycleCommand>(existing.RequestJson)!)) return null;
            // Mirrors SurrealNftLifecycleStore preconditions and fenced witness selection, including direct-credential expiry.
            var participantRenewal = existing.StageConsentsJson != row.StageConsentsJson && NftStageConsentIdentity.CanRenew(existing, row);
            if (existing.StageConsentsJson != row.StageConsentsJson
                && (!requireActionAuthority || !(participantRenewal || NftStageConsentIdentity.CanAppend(existing, row)))) return null;
            if (existing.ReplacementCostConsentsJson != row.ReplacementCostConsentsJson
                && (!requireActionAuthority || !NftReplacementCostContract.CanAppend(existing, row))) return null;
            if (existing.ReplacementCostRetirementsJson != row.ReplacementCostRetirementsJson
                && !NftReplacementCostContract.CanRetire(existing, row)) return null;
            if (existing.ActorContinuationsJson != row.ActorContinuationsJson
                && (!requireActionAuthority || !NftActorContinuationIdentity.CanAppend(existing, row))) return null;
            var stageCostsAdded = existing.StageCostConsentsJson != row.StageCostConsentsJson;
            if (stageCostsAdded && (!requireActionAuthority || !NftStageCostContract.CanAppend(existing, row))) return null;
            if (requireActionAuthority && !NftStageCostContract.AdmitsWrite(existing, row)) return null;
            // Mirrors the receipt's database-time expiry fenced in the same transaction as the stage cost acceptance.
            if (stageCostsAdded && NftStageCostContract.Current(row)!.Quote.ExpiresAt <= Now()) return null;
            if (existing.ReplacementCostRetirementsJson != row.ReplacementCostRetirementsJson
                && NftEffectAuthorities.Lapsed(NftReplacementCostContract.Retirements(row).Last().Authority, Now())) return null;
            var write = existing.ActorContinuationsJson != row.ActorContinuationsJson ? NftLifecycleWrite.RequesterRenewal
                : participantRenewal ? NftLifecycleWrite.ParticipantRenewal
                : existing.ReplacementCostConsentsJson != row.ReplacementCostConsentsJson ? NftLifecycleWrite.CostAcceptance
                : stageCostsAdded ? NftLifecycleWrite.StageCostAcceptance : NftLifecycleWrite.Effect;
            if (requireActionAuthority && !Authorized(row, write)) return null;
            // Mirrors the superseded-consent guard for expiry only; revocation is a database fact this double does not model.
            if (write == NftLifecycleWrite.ParticipantRenewal && (NftStageConsentIdentity.Superseded(row) is not { } superseded
                || !NftEffectAuthorities.Lapsed(superseded.Authority, Now()))) return null;
            var updated = Copy(row);
            updated.Version++;
            _rows[row.Id] = updated;
            return Copy(updated);
        }
    }
    public Task<IReadOnlyList<NftLifecycle>> ListPendingAsync(int limit, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult<IReadOnlyList<NftLifecycle>>(_rows.Values
            .Where(row => row.State is "Submitted" or "NeedsOperator" or "Confirmed")
            .OrderBy(row => row.UpdatedAt).Take(limit).Select(Copy).ToArray());
    }
    public Task<IReadOnlyList<NftLifecycle>> ListParticipantSnapshotAsync(Guid participant, DateTimeOffset snapshot, int offset, int limit, CancellationToken ct = default)
    {
        lock (_sync) return Task.FromResult<IReadOnlyList<NftLifecycle>>(_rows.Values.Where(row => (row.ActorUserId == participant.ToString("N") || row.RecipientUserId == participant.ToString("N")) && row.CreatedAt <= snapshot)
            .OrderByDescending(row => row.CreatedAt).ThenBy(row => row.Id, StringComparer.Ordinal).Skip(offset).Take(limit).Select(Copy).ToArray());
    }
    /// <summary>Stands in for the wallet table's chain/address index; the harness wires it to its registered wallets.</summary>
    internal Func<string, string, IReadOnlyList<Guid>> CustodialWallets { get; set; } = (_, _) => [];
    public Task<IReadOnlyList<Guid>> FindCustodialWalletIdsAsync(string chainType, string address, CancellationToken ct = default)
        => Task.FromResult(CustodialWallets(chainType, address));
    /// <summary>Raised by every authority read when set, standing in for a store outage.</summary>
    internal Exception? AuthorityLookupFault { get; set; }
    /// <summary>Rewrites a stored asset authority, standing in for a legacy or partially recorded row.</summary>
    internal void MutateAuthority(string nftId, Action<NftAssetAuthority> mutate)
    {
        lock (_sync) mutate(_assets[nftId]);
    }
    public Task<NftAssetAuthority?> GetAuthorityAsync(string nftId, CancellationToken ct = default)
    {
        if (AuthorityLookupFault is { } fault) throw fault;
        lock (_sync) return Task.FromResult(_assets.TryGetValue(nftId, out var row) ? Copy(row) : null);
    }
    public Task<bool> ReserveAuthorityAsync(NftAssetAuthority authority, NftLifecycle lifecycle, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_rows.TryGetValue(lifecycle.Id, out var intent) || intent.Version != lifecycle.Version || intent.State != "Preparing"
                || !_assets.TryGetValue(authority.NftId, out var current) || current.Version != authority.Version || current.Burned
                || (current.ActiveLifecycleId is not null && current.ActiveLifecycleId != lifecycle.Id)) return Task.FromResult(false);
            current.ActiveLifecycleId = lifecycle.Id;
            return Task.FromResult(true);
        }
    }
    public Task<bool> CancelUnsentAsync(NftLifecycle expected, NftAssetAuthority? authority,
        AZOA.WebAPI.Models.Signing.SigningConsentWitness consent, CancellationToken ct = default)
    {
        var command = JsonSerializer.Deserialize<AZOA.WebAPI.Models.Blockchain.NftLifecycleCommand>(expected.RequestJson)!;
        var expectedTenant = NftActorContinuationIdentity.TakenOver(expected, command) ? Guid.Empty : command.ActingTenantId;
        if (consent.Credential.ActorUserId != command.Actor || (consent.Credential.TenantId ?? Guid.Empty) != expectedTenant
            || consent.Credential.ExpiresAt is { } expires && expires <= Now()) return Task.FromResult(false);
        lock (_sync)
        {
            if (!_rows.TryGetValue(expected.Id, out var current) || current.Version != expected.Version || current.ConfirmedStageCount != 0
                || current.State is not ("Preparing" or "Prepared" or "AwaitingRecipientAcceptance" or "AwaitingRefundAuthorization" or "AwaitingRefundDestinationAcceptance")) return Task.FromResult(false);
            current.State = "Cancelled";
            current.Version++;
            if (authority is not null && _assets.TryGetValue(authority.NftId, out var asset) && asset.ActiveLifecycleId == expected.Id) asset.ActiveLifecycleId = null;
            return Task.FromResult(true);
        }
    }
    public Task<bool> CommitAsync(NftLifecycle expected, NftAssetAuthority authority, NftOwnership ownership, CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (!_rows.TryGetValue(expected.Id, out var current) || current.Version != expected.Version || current.State != "Confirmed") return Task.FromResult(false);
            if (expected.Operation != "Mint" && (!_assets.TryGetValue(authority.NftId, out var prior)
                || prior.Version != authority.Version - 1 || prior.ActiveLifecycleId != expected.Id)) return Task.FromResult(false);
            if (expected.Operation == "Mint" && _assets.Values.Any(row => row.ChainType == authority.ChainType && row.Network == authority.Network
                && row.ProtocolIdentity == authority.ProtocolIdentity && row.ContractAddress == authority.ContractAddress && row.AssetId == authority.AssetId)) return Task.FromResult(false);
            current.State = "Completed";
            current.Version++;
            _assets[authority.NftId] = Copy(authority);
            Ownership[ownership.Id] = Copy(ownership);
            return Task.FromResult(true);
        }
    }
    public Task<CredentialOwnershipRecord?> FindCredentialAsync(string chain, string network, string assetId, string? contractAddress, CancellationToken ct = default)
    {
        if (AuthorityLookupFault is { } fault) throw fault;
        lock (_sync)
        {
            var candidates = _assets.Values.Where(row => row.ChainType == chain && row.Network == network && row.AssetId == assetId
                && !row.Burned && !row.Revoked && (contractAddress is null || row.ContractAddress == contractAddress)).ToArray();
            if (candidates.Length != 1) return Task.FromResult<CredentialOwnershipRecord?>(null);
            var asset = candidates[0];
            var verified = _rows.TryGetValue(asset.IssuanceLifecycleId, out var issuance)
                && issuance.State == "Completed" && issuance.Operation == "Mint"
                && issuance.ActorUserId == asset.IssuerUserId && issuance.ActorUserId != issuance.RecipientUserId
                && issuance.NftId == asset.NftId && issuance.AssetId == asset.AssetId
                && issuance.ProtocolIdentity == asset.ProtocolIdentity && issuance.ContractAddress == asset.ContractAddress
                && issuance.CreationTransactionId == asset.CreationTransactionId;
            return Task.FromResult<CredentialOwnershipRecord?>(new()
            {
                Id = Guid.Parse(asset.NftId), HolderUserId = Guid.Parse(asset.HolderUserId), HolderAddress = asset.OwnerAddress,
                IssuerUserId = Guid.TryParse(asset.IssuerUserId, out var issuer) ? issuer : null,
                ChainType = chain, Network = network, TokenId = assetId, ContractAddress = asset.ContractAddress,
                ProtocolIdentity = asset.ProtocolIdentity, IsSoulbound = asset.Soulbound, HasVerifiedIssuance = verified,
            });
        }
    }
    private bool Authorized(NftLifecycle row, NftLifecycleWrite write)
    {
        var command = JsonSerializer.Deserialize<AZOA.WebAPI.Models.Blockchain.NftLifecycleCommand>(row.RequestJson)!;
        var required = NftEffectAuthorities.Select(row, command, write);
        return required is not null && required.All(requirement => !NftEffectAuthorities.Lapsed(requirement.Authority, Now()));
    }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
