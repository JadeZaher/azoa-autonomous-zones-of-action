using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IWalletManager
{
    Task<AZOAResult<IWallet>> GetAsync(Guid id, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IWallet>>> QueryAsync(WalletQueryRequest query, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IWallet>> CreateAsync(WalletCreateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IWallet>> UpdateAsync(Guid id, WalletUpdateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> SetDefaultAsync(Guid userId, Guid walletId, AZOARequest? request = null);
    Task<AZOAResult<PortfolioResult>> GetPortfolioAsync(Guid walletId, Guid userId, AZOARequest? request = null);

    /// <summary>
    /// Generate a new wallet on the platform for a chain (creates keypair, stores encrypted).
    /// </summary>
    Task<AZOAResult<IWallet>> GenerateWalletAsync(WalletGenerateRequest model, Guid userId, AZOARequest? request = null);

    /// <summary>Idempotently creates or returns the platform wallet for a user and chain.</summary>
    Task<AZOAResult<IWallet>> BootstrapWalletAsync(WalletGenerateRequest model, Guid userId, AZOARequest? request = null);

    /// <summary>
    /// Connect an external wallet (e.g., MetaMask) by verifying signed message ownership.
    /// </summary>
    Task<AZOAResult<IWallet>> ConnectWalletAsync(WalletConnectRequest model, Guid userId, AZOARequest? request = null);

    /// <summary>
    /// Export a platform-generated wallet's private key and seed phrase.
    /// Requires verification of user ownership.
    /// </summary>
    Task<AZOAResult<WalletExportResult>> ExportWalletAsync(Guid walletId, Guid userId, AZOARequest? request = null);

    /// <summary>
    /// Top-up (faucet-fund) a wallet with test tokens on a dev / test network.
    /// HARD GUARD: never dispenses on mainnet. Requires user ownership of the wallet.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client-supplied idempotency key (e.g. the <c>Idempotency-Key</c>
    /// request header). When provided it is used verbatim as the faucet dispense
    /// idempotency key so a retried <c>POST /topup</c> dispenses exactly once.
    /// When null the faucet derives a deterministic content key from
    /// (chain, recipient, amount) — so absence is still dedup-safe (no random
    /// per-request key is ever generated).
    /// </param>
    Task<AZOAResult<object>> TopUpAsync(Guid walletId, decimal? amount, Guid userId, AZOARequest? request = null, string? clientIdempotencyKey = null);
}
