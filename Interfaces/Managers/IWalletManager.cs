using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IWalletManager
{
    Task<OASISResult<IWallet>> GetAsync(Guid id, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IWallet>>> QueryAsync(WalletQueryRequest query, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IWallet>> CreateAsync(WalletCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IWallet>> UpdateAsync(Guid id, WalletUpdateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> SetDefaultAsync(Guid avatarId, Guid walletId, OASISRequest? request = null);
    Task<OASISResult<PortfolioResult>> GetPortfolioAsync(Guid walletId, Guid avatarId, OASISRequest? request = null);

    /// <summary>
    /// Generate a new wallet on the platform for a chain (creates keypair, stores encrypted).
    /// </summary>
    Task<OASISResult<IWallet>> GenerateWalletAsync(WalletGenerateRequest model, Guid avatarId, OASISRequest? request = null);

    /// <summary>
    /// Connect an external wallet (e.g., MetaMask) by verifying signed message ownership.
    /// </summary>
    Task<OASISResult<IWallet>> ConnectWalletAsync(WalletConnectRequest model, Guid avatarId, OASISRequest? request = null);

    /// <summary>
    /// Export a platform-generated wallet's private key and seed phrase.
    /// Requires verification of avatar ownership.
    /// </summary>
    Task<OASISResult<WalletExportResult>> ExportWalletAsync(Guid walletId, Guid avatarId, OASISRequest? request = null);

    /// <summary>
    /// Top-up (faucet-fund) a wallet with test tokens on a dev / test network.
    /// HARD GUARD: never dispenses on mainnet. Requires avatar ownership of the wallet.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client-supplied idempotency key (e.g. the <c>Idempotency-Key</c>
    /// request header). When provided it is used verbatim as the faucet dispense
    /// idempotency key so a retried <c>POST /topup</c> dispenses exactly once.
    /// When null the faucet derives a deterministic content key from
    /// (chain, recipient, amount) — so absence is still dedup-safe (no random
    /// per-request key is ever generated).
    /// </param>
    Task<OASISResult<object>> TopUpAsync(Guid walletId, decimal? amount, Guid avatarId, OASISRequest? request = null, string? clientIdempotencyKey = null);
}
