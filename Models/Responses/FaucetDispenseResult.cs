namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Outcome of a provider-scoped faucet dispense (dev / test networks only). The
/// faucet is a per-chain capability defined on <see cref="AZOA.WebAPI.Interfaces.IBlockchainProvider"/>,
/// so the shape is uniform across chains even though the mechanics differ:
/// <list type="bullet">
/// <item>A server-side dispense (Algorand) carries the submitted
/// <see cref="TxHash"/> and <see cref="IsClientSide"/> is <c>false</c>.</item>
/// <item>A client-side faucet (Solana RPC airdrop) carries a null
/// <see cref="TxHash"/> and <see cref="IsClientSide"/> is <c>true</c> — the
/// caller (frontend) performs the airdrop; the backend only acknowledges.</item>
/// </list>
/// </summary>
public sealed record FaucetDispenseResult(string? TxHash, bool IsClientSide, string Message);
