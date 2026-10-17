using System.Security.Cryptography;
using System.Text;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Row = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletOwnershipProof;

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
    public async Task<WalletOwnershipProof> RecordAsync(WalletOwnershipProof proof, CancellationToken ct = default)
    {
        if (proof.UserId == Guid.Empty || proof.WalletId == Guid.Empty
            || !WalletAuthIdentity.TryCreate(proof.Address, proof.ChainType, out var canonicalAddress, out var canonicalChain))
            throw new ArgumentException("A canonical wallet identity, owner and wallet are required.", nameof(proof));
        var row = new Row
        {
            Id = RecordId(canonicalChain, canonicalAddress),
            UserId = SurrealLink.ToLink("user", proof.UserId.ToString("N"))!,
            ChainType = canonicalChain,
            Address = canonicalAddress,
            WalletId = proof.WalletId.ToString("N"),
            ProvenAt = new DateTimeOffset(DateTime.SpecifyKind(proof.ProvenAt, DateTimeKind.Utc)),
        };
        var response = await executor.ExecuteAsync(SurrealWriter.Upsert(row), ct);
        response.EnsureAllOk();
        return ToDomain(response.GetValues<Row>(0).Single());
    }

    private static string RecordId(string chain, string address)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{chain}:{address}"))).ToLowerInvariant();

    private static WalletOwnershipProof ToDomain(Row row) => new(SurrealRecordGuid.Parse(row.UserId),
        SurrealRecordGuid.Parse(row.WalletId), row.ChainType, row.Address, row.ProvenAt.UtcDateTime);
}
