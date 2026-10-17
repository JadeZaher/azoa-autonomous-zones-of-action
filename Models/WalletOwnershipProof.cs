namespace AZOA.WebAPI.Models;

/// <summary>A user proved control of <see cref="Address"/> on <see cref="ChainType"/> (canonical form) by signing a wallet-auth challenge.</summary>
public sealed record WalletOwnershipProof(Guid UserId, Guid WalletId, string ChainType, string Address, DateTime ProvenAt);
