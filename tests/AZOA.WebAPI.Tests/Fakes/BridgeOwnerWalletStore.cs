using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Tests.Fakes;

/// <summary>Every user owns a custodial (Platform) wallet at each fixture refund address on each fixture chain,
/// so bridge reversal tests about idempotency/recovery pass the audit-2 M8 recipient-ownership check.</summary>
internal sealed class BridgeOwnerWalletStore(params string[] extraAddresses) : IWalletStore
{
    private static readonly string[] Chains = ["Solana", "Algorand", "Ethereum", "SimulatedA", "SimulatedB"];
    private static readonly string[] Addresses = ["refund_addr", "refund-address", "source_refund_addr", "src_refund_addr", "source-refund-addr", "source-refund"];

    public Task<AZOAResult<IEnumerable<IWallet>>> GetByUserAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<IEnumerable<IWallet>>.Success(Addresses.Concat(extraAddresses)
            .SelectMany(address => Chains.Select(chain => (IWallet)new Wallet
            {
                Id = Guid.NewGuid(), UserId = userId, ChainType = chain, Address = address, WalletType = WalletType.Platform,
            })).ToArray()));

    public Task<AZOAResult<IWallet>> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AZOAResult<IEnumerable<IWallet>>> GetAllAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AZOAResult<IWallet>> UpsertAsync(IWallet wallet, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AZOAResult<IWallet>> CreateIfAbsentAsync(IWallet wallet, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AZOAResult<IWallet>> BootstrapTenantAsync(IWallet wallet, string externalUserId, SigningConsentWitness authority,
        CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
}
