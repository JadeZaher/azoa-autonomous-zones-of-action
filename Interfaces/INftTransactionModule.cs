using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Provider-owned NFT preparation, exact-byte rebroadcast, and authoritative observation.</summary>
public interface INftTransactionModule : IBlockchainProviderModule
{
    /// <summary>Resolves the configured credential authority without exposing signing material.</summary>
    string? CredentialAuthorityAddress { get; }

    /// <summary>Names the chain-native token standard established by this module.</summary>
    string NftTokenStandard { get; }
    /// <summary>Names native fee currency for a quote without implying a fiat valuation.</summary>
    string NativeCurrencyCode { get; }
    /// <summary>Number of decimal places in one native currency unit.</summary>
    int NativeCurrencyDecimals { get; }

    /// <summary>Builds and signs without broadcasting; the caller must persist the returned identity before submission.</summary>
    Task<AZOAResult<PreparedNftTransaction>> PrepareNftTransactionAsync(NftTransactionIntent intent, CancellationToken ct = default);

    /// <summary>Submits only the persisted bytes within their validity window; repeats retain the same chain transaction identity.</summary>
    Task SubmitNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default);

    /// <summary>Observes the persisted transaction identity without submitting or rebuilding it.</summary>
    Task<AZOAResult<NftTransactionObservation>> ObserveNftTransactionAsync(PreparedNftTransaction prepared, CancellationToken ct = default);

    /// <summary>Reads exact asset ownership from chain state; unavailable evidence never grants application access.</summary>
    Task<AZOAResult<NftOwnershipObservation>> ReadNftOwnershipAsync(string assetId, string ownerAddress, string protocolIdentity, CancellationToken ct = default, string contractAddress = "");
}
