using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Public marketplace-safe projection of an avatar: no PII (Email, FirstName,
/// LastName) and no auth material (AuthWalletAddress/ChainType). Returned for
/// any avatar that isn't the requesting caller — see Controllers/AvatarController.cs.
/// </summary>
public class PublicAvatarInfo
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Title { get; set; }

    public static PublicAvatarInfo From(IAvatar avatar) => new()
    {
        Id = avatar.Id,
        Username = avatar.Username,
        Title = avatar.Title,
    };
}
