using OASIS.WebAPI.Models;

namespace OASIS.WebAPI.Interfaces;

public interface IOASISStorageProviderNFTExtensions
{
    // Avatar NFT Management
    Task<OASISResult<IAvatarNFT>> SaveAvatarNFTAsync(IAvatarNFT avatarNFT);
    Task<OASISResult<IAvatarNFT>> LoadAvatarNFTAsync(Guid id);
    Task<OASISResult<IAvatarNFT>> LoadAvatarNFTByTokenIdAsync(string chainType, string nftContractAddress, string tokenId);
    Task<OASISResult<IEnumerable<IAvatarNFT>>> LoadAvatarNFTsByAvatarAsync(Guid avatarId);
    Task<OASISResult<bool>> DeleteAvatarNFTAsync(Guid id);
    
    // Holon NFT Binding Management
    Task<OASISResult<IHolonNFTBinding>> SaveHolonNFTBindingAsync(IHolonNFTBinding binding);
    Task<OASISResult<IHolonNFTBinding>> LoadHolonNFTBindingAsync(Guid id);
    Task<OASISResult<IEnumerable<IHolonNFTBinding>>> LoadHolonNFTBindingsByAvatarNFTAsync(Guid avatarNFTId);
    Task<OASISResult<bool>> DeleteHolonNFTBindingAsync(Guid id);
    
    // Wallet NFT Binding Management
    Task<OASISResult<IWalletNFTBinding>> SaveWalletNFTBindingAsync(IWalletNFTBinding binding);
    Task<OASISResult<IWalletNFTBinding>> LoadWalletNFTBindingAsync(Guid id);
    Task<OASISResult<IEnumerable<IWalletNFTBinding>>> LoadWalletNFTBindingsByAvatarNFTAsync(Guid avatarNFTId);
    Task<OASISResult<bool>> DeleteWalletNFTBindingAsync(Guid id);
}