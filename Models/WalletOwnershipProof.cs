namespace AZOA.WebAPI.Models;

/// <summary>A user proved control of <see cref="Address"/> on <see cref="ChainType"/> (canonical form) by signing a purpose-bound ownership challenge.</summary>
public sealed record WalletOwnershipProof(Guid UserId, Guid WalletId, string ChainType, string Address, DateTime ProvenAt);

/// <summary>A recorded proof and the user whose stale proof it superseded, if any.</summary>
public sealed record WalletOwnershipProofWrite(WalletOwnershipProof Proof, Guid? SupersededUserId);
