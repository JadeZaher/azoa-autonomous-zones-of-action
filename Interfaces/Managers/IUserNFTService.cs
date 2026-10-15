using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface IUserNFTService
{
    // User NFT Management
    Task<AZOAResult<IUserNFT>> MintUserNFTAsync(Guid userId, UserNFTMintModel model, AZOARequest? request = null);

    /// <summary>D41 issuance: <paramref name="issuerUserId"/> issues a credential TO <paramref name="recipientUserId"/>.</summary>
    /// <remarks>
    /// The recipient becomes the row's holder (<c>user_id</c>) and the issuer is stamped from the
    /// authenticated caller the controller passes here -- there is no issuer field on
    /// <see cref="UserNFTMintModel"/>, because a body-supplied issuer would let any caller nominate
    /// itself the revocation authority over a credential it did not issue. Refuses when the two are
    /// the same party, and refuses when the recipient's address on the credential's chain cannot be
    /// proven. See Services/Users/AGENTS.md.
    /// </remarks>
    Task<AZOAResult<IUserNFT>> MintUserNFTAsync(Guid issuerUserId, Guid recipientUserId, UserNFTMintModel model, AZOARequest? request = null);
    Task<AZOAResult<IUserNFT>> GetUserNFTAsync(Guid id, AZOARequest? request = null);
    Task<AZOAResult<IUserNFT>> GetUserNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IUserNFT>>> GetUserNFTsByUserAsync(Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> TransferUserNFTAsync(Guid id, string recipientAddress, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> BurnUserNFTAsync(Guid id, Guid userId, AZOARequest? request = null);

    // Holon NFT Binding Management
    Task<AZOAResult<IHolonNFTBinding>> BindHolonToUserNFTAsync(Guid holonId, Guid userNFTId, HolonNFTBindingModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IHolonNFTBinding>>> GetHolonBindingsAsync(Guid userNFTId, AZOARequest? request = null);
    Task<AZOAResult<bool>> UpdateHolonBindingAsync(Guid id, HolonNFTBindingUpdateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveHolonBindingAsync(Guid id, Guid userId, AZOARequest? request = null);

    // Wallet NFT Binding Management
    Task<AZOAResult<IWalletNFTBinding>> BindWalletToUserNFTAsync(Guid walletId, Guid userNFTId, WalletNFTBindingModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<IWalletNFTBinding>>> GetWalletBindingsAsync(Guid userNFTId, AZOARequest? request = null);
    Task<AZOAResult<bool>> UpdateWalletBindingAsync(Guid id, WalletNFTBindingUpdateModel model, Guid userId, AZOARequest? request = null);
    Task<AZOAResult<bool>> RemoveWalletBindingAsync(Guid id, Guid userId, AZOARequest? request = null);
    
    // Composite Operations
    Task<AZOAResult<UserNFTCompositeResult>> GetUserNFTCompositeAsync(Guid userNFTId, AZOARequest? request = null);
    Task<AZOAResult<IEnumerable<UserNFTCompositeResult>>> GetUserNFTCompositesByUserAsync(Guid userId, AZOARequest? request = null);
    
    // Verification and Authorization
    Task<AZOAResult<bool>> VerifyUserNFTOwnershipAsync(Guid userId, string chainType, string nftContractAddress, string tokenId, AZOARequest? request = null);
    Task<AZOAResult<bool>> VerifyHolonAccessAsync(Guid userNFTId, Guid holonId, string requiredPermission, AZOARequest? request = null);
    Task<AZOAResult<bool>> VerifyWalletAccessAsync(Guid userNFTId, Guid walletId, string requiredAccess, AZOARequest? request = null);
}