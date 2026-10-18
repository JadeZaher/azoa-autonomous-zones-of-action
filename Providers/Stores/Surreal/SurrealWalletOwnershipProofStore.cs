using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletOwnershipProof;
using WalletRow = AZOA.WebAPI.Persistence.SurrealDb.Models.Wallet;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed class SurrealWalletOwnershipProofStore(ISurrealExecutor executor) : IWalletOwnershipProofStore
{
    /// <inheritdoc/>
    public async Task<WalletOwnershipProof?> GetAsync(string chainType, string address, CancellationToken ct = default)
    {
        if (!WalletAuthIdentity.TryCreate(address, chainType, out var canonicalAddress, out var canonicalChain)) return null;
        var row = await executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(RecordId(canonicalChain, canonicalAddress)), ct);
        return row is null || row.ChainType != canonicalChain || row.Address != canonicalAddress ? null : ToDomain(row);
    }

    /// <inheritdoc/>
    public async Task<bool> IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken ct = default)
    {
        if (walletId == Guid.Empty || ownerUserId == Guid.Empty) return false;
        var wallet = await OwnedExternalWalletAsync(walletId, ownerUserId, ct);
        return wallet is not null && await GetAsync(wallet.ChainType, wallet.Address, ct) is { } proof && proof.UserId == ownerUserId;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<WalletOwnershipProofWrite>> RecordAsync(WalletOwnershipProof proof, CancellationToken ct = default)
    {
        if (proof.UserId == Guid.Empty || proof.WalletId == Guid.Empty
            || !WalletAuthIdentity.TryCreate(proof.Address, proof.ChainType, out var canonicalAddress, out var canonicalChain))
            throw new ArgumentException("A canonical wallet identity, owner and wallet are required.", nameof(proof));
        var id = RecordId(canonicalChain, canonicalAddress);
        var userLink = SurrealLink.ToLink("user", proof.UserId.ToString("N"))!;
        var walletId = proof.WalletId.ToString("N");
        var provenAt = new DateTimeOffset(DateTime.SpecifyKind(proof.ProvenAt, DateTimeKind.Utc));
        var current = await executor.QuerySingleAsync<Row>(SurrealQuery<Row>.Key(id), ct);
        if (current is null)
        {
            try
            {
                var created = await executor.ExecuteAsync(SurrealWriter.Create(new Row
                {
                    Id = id, UserId = userLink, ChainType = canonicalChain, Address = canonicalAddress, WalletId = walletId, ProvenAt = provenAt,
                }), ct);
                created.EnsureAllOk();
                return AZOAResult<WalletOwnershipProofWrite>.Success(new(ToDomain(created.GetValues<Row>(0).Single()), null));
            }
            catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
            {
                return AZOAResult<WalletOwnershipProofWrite>.Failure(Contended);
            }
        }

        var holder = SurrealRecordGuid.Parse(current.UserId);
        if (holder != proof.UserId && await HolderProofIsValidAsync(current, holder, ct))
            return AZOAResult<WalletOwnershipProofWrite>.Failure(HeldByAnotherUser);
        var response = await executor.ExecuteAsync(SurrealWriter.UpdateOnly<Row>(id)
            .Where(saved => saved.UserId == current.UserId && saved.WalletId == current.WalletId)
            .Set(saved => saved.UserId, userLink).Set(saved => saved.WalletId, walletId).Set(saved => saved.ProvenAt, provenAt)
            .Build(), ct);
        response.EnsureAllOk();
        var persisted = response.GetValues<Row>(0).SingleOrDefault();
        return persisted is null
            ? AZOAResult<WalletOwnershipProofWrite>.Failure(Contended)
            : AZOAResult<WalletOwnershipProofWrite>.Success(new(ToDomain(persisted), holder == proof.UserId ? null : holder));
    }

    /// <summary>Refusal while another user's proof is still valid.</summary>
    public const string HeldByAnotherUser = "This address is proven by another account whose wallet still holds it; that account must remove the wallet first.";

    private const string Contended = "The ownership proof for this address changed during the request; retry.";

    /// <summary>A holder's proof stays valid while the wallet it names is still that holder's External wallet for the same canonical identity.</summary>
    private async Task<bool> HolderProofIsValidAsync(Row current, Guid holder, CancellationToken ct)
        => Guid.TryParse(current.WalletId, out var walletId)
            && await OwnedExternalWalletAsync(walletId, holder, ct) is { } wallet
            && WalletAuthIdentity.TryCreate(wallet.Address, wallet.ChainType, out var address, out var chain)
            && address == current.Address && chain == current.ChainType;

    private async Task<WalletRow?> OwnedExternalWalletAsync(Guid walletId, Guid ownerUserId, CancellationToken ct)
    {
        var wallet = await executor.QuerySingleAsync<WalletRow>(SurrealQuery<WalletRow>.Key(walletId.ToString("N")), ct);
        return wallet is not null && wallet.WalletType == WalletRow.WalletTypeKind.External
            && SurrealRecordGuid.Parse(wallet.UserId) == ownerUserId ? wallet : null;
    }

    private static string RecordId(string chain, string address)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{chain}:{address}"))).ToLowerInvariant();

    private static WalletOwnershipProof ToDomain(Row row) => new(SurrealRecordGuid.Parse(row.UserId),
        SurrealRecordGuid.Parse(row.WalletId), row.ChainType, row.Address, row.ProvenAt.UtcDateTime);
}
