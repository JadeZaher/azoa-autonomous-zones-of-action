namespace AZOA.WebAPI.Interfaces.Wtt;

/// <summary>Cross-user recipient lookup so a route needing a participant step never waits on another user's custody.</summary>
public interface IWttRecipientDirectory
{
    /// <summary>Whether the exact canonical address is a key-custodied wallet of a user other than <paramref name="actor"/>.</summary>
    Task<bool> IsAnotherUsersCustodialWalletAsync(Guid actor, string chainType, string address, CancellationToken ct = default);
}

/// <summary>Signature-verified ownership of a connected External wallet (L4 wallet ownership challenge; see
/// Services/Wormhole/AGENTS.md §Recipient rule).</summary>
public interface IWalletOwnershipProof
{
    /// <summary>True only when a verified signature challenge proved <paramref name="ownerUserId"/> controls the wallet.</summary>
    Task<bool> IsSignatureProvenAsync(Guid walletId, Guid ownerUserId, CancellationToken ct = default);
}

/// <summary>Marker: the module signs with protected per-stage keys, so new admissions wait on keyring readiness.</summary>
public interface IWttStageKeyModule : IBlockchainProviderModule;
