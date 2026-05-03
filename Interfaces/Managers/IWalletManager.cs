using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IWalletManager
{
    Task<OASISResult<IWallet>> GetAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IWallet>>> QueryAsync(WalletQueryRequest query, OASISRequest? request = null);
    Task<OASISResult<IWallet>> CreateAsync(WalletCreateModel model, Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<IWallet>> UpdateAsync(Guid id, WalletUpdateModel model, OASISRequest? request = null);
    Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<bool>> SetDefaultAsync(Guid avatarId, Guid walletId, OASISRequest? request = null);
    Task<OASISResult<PortfolioResult>> GetPortfolioAsync(Guid walletId, OASISRequest? request = null);
}
