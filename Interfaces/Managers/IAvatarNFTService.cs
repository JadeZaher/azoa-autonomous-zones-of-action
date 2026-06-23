using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IAvatarNFTService
{
    // Avatar NFT Management
    Task<AZOAResult<IAvatarNFT>> MintAvatarNFTAsync(Guid avatarId, AvatarNFTMintModel model, AZOARequest? request = null);
    Task<AZOAResult<IAvatarNFT>> GetAvatarNFTAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IAvatarNFT>> GetAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IAvatarNFT>>> GetAvatarNFTsByAvatarAsync(Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> TransferAvatarNFTAsync(Guid id, string recipientAddress, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> BurnAvatarNFTAsync(Guid id, Guid avatarId, AZOARequest? request = null);

    // Holon NFT Binding Management
    Task<AZOAResult<IHolonNFTBinding>> BindHolonToAvatarNFTAsync(Guid holonId, Guid avatarNFTId, HolonNFTBindingModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid avatarNFTId, AZOARequest? request = null);
    Task<AZOAResult<bool>> UpdateHolonBindingAsync(Guid id, HolonNFTBindingUpdateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveHolonBindingAsync(Guid id, Guid avatarId, AZOARequest? request = null);

    // Wallet NFT Binding Management
    Task<AZOAResult<IWalletNFTBinding>> BindWalletToAvatarNFTAsync(Guid walletId, Guid avatarNFTId, WalletNFTBindingModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletBindingsAsync(Guid avatarNFTId, AZOARequest? request = null);
    Task<AZOAResult<bool>> UpdateWalletBindingAsync(Guid id, WalletNFTBindingUpdateModel model, Guid avatarId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveWalletBindingAsync(Guid id, Guid avatarId, AZOARequest? request = null);
    
    // Composite Operations
    Task<AZOAResult<AvatarNFTCompositeResult>> GetAvatarNFTCompositeAsync(Guid avatarNFTId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<AvatarNFTCompositeResult>>> GetAvatarNFTCompositesByAvatarAsync(Guid avatarId, AZOARequest? request = null);
    
    // Verification and Authorization
    Task<AZOAResult<bool>> VerifyAvatarNFTOwnershipAsync(Guid avatarId, string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null);
    Task<AZOAResult<bool>> VerifyHolonAccessAsync(Guid avatarNFTId, Guid holonId, string requiredPermission, AZOARequest? request = null);
    Task<AZOAResult<bool>> VerifyWalletAccessAsync(Guid avatarNFTId, Guid walletId, string requiredAccess, AZOARequest? request = null);
}