using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Interfaces.Managers;

public interface IAvatarNFTService
{
    // Avatar NFT Management
    Task<OASISResult<IAvatarNFT>> MintAvatarNFTAsync(Guid avatarId, AvatarNFTMintModel model, OASISRequest? request = null);
    Task<OASISResult<IAvatarNFT>> GetAvatarNFTAsync(Guid id, OASISRequest? request = null);
    Task<OASISResult<IAvatarNFT>> GetAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IAvatarNFT>>> GetAvatarNFTsByAvatarAsync(Guid avatarId, OASISRequest? request = null);
    Task<OASISResult<bool>> TransferAvatarNFTAsync(Guid id, string recipientAddress, OASISRequest? request = null);
    Task<OASISResult<bool>> BurnAvatarNFTAsync(Guid id, OASISRequest? request = null);
    
    // Holon NFT Binding Management
    Task<OASISResult<IHolonNFTBinding>> BindHolonToAvatarNFTAsync(Guid holonId, Guid avatarNFTId, HolonNFTBindingModel model, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid avatarNFTId, OASISRequest? request = null);
    Task<OASISResult<bool>> UpdateHolonBindingAsync(Guid id, HolonNFTBindingUpdateModel model, OASISRequest? request = null);
    Task<OASISResult<bool>> RemoveHolonBindingAsync(Guid id, OASISRequest? request = null);
    
    // Wallet NFT Binding Management
    Task<OASISResult<IWalletNFTBinding>> BindWalletToAvatarNFTAsync(Guid walletId, Guid avatarNFTId, WalletNFTBindingModel model, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<IWalletNFTBinding>> GetWalletBindingsAsync(Guid avatarNFTId, OASISRequest? request = null);
    Task<OASISResult<bool>> UpdateWalletBindingAsync(Guid id, WalletNFTBindingUpdateModel model, OASISRequest? request = null);
    Task<OASISResult<bool>> RemoveWalletBindingAsync(Guid id, OASISRequest? request = null);
    
    // Composite Operations
    Task<OASISResult<AvatarNFTCompositeResult>> GetAvatarNFTCompositeAsync(Guid avatarNFTId, OASISRequest? request = null);
    Task<OASISResult<IEnumerable<AvatarNFTCompositeResult>> GetAvatarNFTCompositesByAvatarAsync(Guid avatarId, OASISRequest? request = null);
    
    // Verification and Authorization
    Task<OASISResult<bool>> VerifyAvatarNFTOwnershipAsync(Guid avatarId, string chainType, string nftContractAddress, string tokenId, OASISRequest? request = null);
    Task<OASISResult<bool>> VerifyHolonAccessAsync(Guid avatarNFTId, Guid holonId, string requiredPermission, OASISRequest? request = null);
    Task<OASISResult<bool>> VerifyWalletAccessAsync(Guid avatarNFTId, Guid walletId, string requiredAccess, OASISRequest? request = null);
}