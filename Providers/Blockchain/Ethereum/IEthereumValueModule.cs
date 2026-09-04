using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>
/// The BigInteger-native Ethereum value surface. The <see cref="IBlockchainProvider"/>
/// methods delegate straight to it — the amount type they carry is already BigInteger
/// base units, so nothing is converted in between.
/// </summary>
/// <remarks>
/// This module used to be the escape hatch from a <c>ulong</c> interface that could not
/// express more than ~18.45 ETH. After the D13/D15 widening to <c>RailAmount</c> the
/// edge conversions are gone and this is simply the typed, wei-exact surface callers
/// use when they already hold a <see cref="BigInteger"/> and want no dimension check.
/// See <c>Providers/Blockchain/Ethereum/AGENTS.md</c> §amount-boundary.
/// </remarks>
public interface IEthereumValueModule : IBlockchainProviderModule
{
    /// <summary>Native ETH balance in wei. An unreachable node is an error, never a zero balance.</summary>
    Task<AZOAResult<BigInteger>> GetNativeBalanceWeiAsync(string address, CancellationToken ct = default);

    /// <summary>ERC-20 balance in the token's base units.</summary>
    Task<AZOAResult<BigInteger>> GetErc20BalanceAsync(string contractAddress, string ownerAddress, CancellationToken ct = default);

    /// <summary>Sends native ETH. Returns the real transaction hash.</summary>
    Task<AZOAResult<string>> TransferNativeAsync(
        string fromAddress, string toAddress, BigInteger weiAmount,
        SigningContext? signingContext = null, CancellationToken ct = default);

    /// <summary>Calls ERC-20 <c>transfer(address,uint256)</c>. Returns the real transaction hash.</summary>
    Task<AZOAResult<string>> TransferErc20Async(
        string contractAddress, string fromAddress, string toAddress, BigInteger amount,
        SigningContext? signingContext = null, CancellationToken ct = default);

    /// <summary>Calls ERC-20 <c>mint(address,uint256)</c>; fails closed when the contract has no such entry point.</summary>
    Task<AZOAResult<string>> MintErc20Async(
        string contractAddress, string minterAddress, string recipientAddress, BigInteger amount,
        SigningContext? signingContext = null, CancellationToken ct = default);

    /// <summary>Calls ERC-20 <c>burn(uint256)</c>; fails closed when the contract has no such entry point.</summary>
    Task<AZOAResult<string>> BurnErc20Async(
        string contractAddress, string holderAddress, BigInteger amount,
        SigningContext? signingContext = null, CancellationToken ct = default);
}
