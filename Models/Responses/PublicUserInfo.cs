using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Public marketplace-safe projection of a user: no PII (Email, FirstName,
/// LastName) and no auth material (AuthWalletAddress/ChainType). Returned for
/// any user that isn't the requesting caller — see Controllers/UserController.cs.
/// </summary>
public class PublicUserInfo
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string? Title { get; set; }

    public static PublicUserInfo From(IUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Title = user.Title,
    };
}
