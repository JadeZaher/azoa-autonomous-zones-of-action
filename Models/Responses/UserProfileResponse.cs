using AZOA.WebAPI.Interfaces;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// The caller's own account as the API returns it: an explicit allowlist, so credential
/// material (password hash, revocation watermark) can never ride along with a new field.
/// See Controllers/AGENTS.md §User responses.
/// </summary>
public sealed class UserProfileResponse
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public DateTime CreatedDate { get; init; }
    public DateTime? LastBeamedInDate { get; init; }
    public bool IsActive { get; init; }
    public bool IsVerified { get; init; }
    public string DappRole { get; init; } = string.Empty;
    public Guid? OwnerTenantId { get; init; }
    public string? ExternalUserId { get; init; }
    public string? ExternalRef { get; init; }
    public string? AuthWalletAddress { get; init; }
    public string? AuthWalletChainType { get; init; }
    /// Opt-in: this user appears in another caller's search results only when true.
    public bool Discoverable { get; init; }

    public static UserProfileResponse From(IUser user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Email = user.Email,
        Title = user.Title,
        FirstName = user.FirstName,
        LastName = user.LastName,
        CreatedDate = user.CreatedDate,
        LastBeamedInDate = user.LastBeamedInDate,
        IsActive = user.IsActive,
        IsVerified = user.IsVerified,
        DappRole = user.DappRole,
        OwnerTenantId = user.OwnerTenantId,
        ExternalUserId = user.ExternalUserId,
        ExternalRef = user.ExternalRef,
        AuthWalletAddress = user.AuthWalletAddress,
        AuthWalletChainType = user.AuthWalletChainType,
        Discoverable = user.Discoverable,
    };

    /// <summary>Projects a manager result onto the wire shape, keeping its error, message and code.</summary>
    public static AZOAResult<UserProfileResponse> From(AZOAResult<IUser> result) => new()
    {
        IsError = result.IsError,
        Message = result.Message,
        Code = result.Code,
        Exception = result.Exception,
        Result = result.Result is null ? null : From(result.Result),
    };
}
