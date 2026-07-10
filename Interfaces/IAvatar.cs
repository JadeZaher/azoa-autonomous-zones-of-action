using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Interfaces;

public interface IAvatar
{
    Guid Id { get; set; }
    string Username { get; set; }
    string Email { get; set; }
    [JsonIgnore]
    string PasswordHash { get; set; }
    string? Title { get; set; }
    string? FirstName { get; set; }
    string? LastName { get; set; }
    DateTime CreatedDate { get; set; }
    DateTime? LastBeamedInDate { get; set; }
    bool IsActive { get; set; }
    bool IsVerified { get; set; }
    string DappRole { get; set; }

    // Tenant ownership (tenant-onboarding). OwnerTenantId is the provisioning
    // tenant principal's avatar id (self-FK); null = not tenant-managed.
    Guid? OwnerTenantId { get; set; }
    string? ExternalUserId { get; set; }
    string? ExternalRef { get; set; }

    // ── user-sovereign-identity ───────────────────────────────────────────────
    // Wallet-challenge auth binding (AC2): the primary external wallet this avatar
    // authenticates with. Set on first wallet-verify (create) and on an authed
    // wallet/link. A (AuthWalletAddress, AuthWalletChainType) pair is unique per
    // avatar and is matched EXACTLY on login — never by email/username/extuser.
    string? AuthWalletAddress { get; set; }
    string? AuthWalletChainType { get; set; }

    // Post-claim custody-window cut (AC3b/H2). Any tenant-driven child JWT (or
    // claim token) issued BEFORE this watermark is rejected at the signing seam and
    // at credential checks, closing the residual 15-min window after a claim. UTC;
    // null = never claimed/revoked-forward.
    DateTime? AuthNotBefore { get; set; }
}
