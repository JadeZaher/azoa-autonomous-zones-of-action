namespace AZOA.WebAPI.Models;

public sealed record MemberHandoffRequest(string DeliveryEmail, string ReturnUrl);
public sealed record MemberHandoffPreview(Guid RequestId, string ClaimToken);
public sealed record MemberHandoffDescriptor(Guid RequestId, Guid TenantId, string TenantName,
    Guid TargetUserId, string Ownership, string[] Scopes, DateTimeOffset GrantExpiresAt,
    string ReturnUrl, DateTimeOffset ExpiresAt);
public sealed record MemberHandoffView(Guid RequestId, Guid TenantId, string TenantName,
    Guid TargetUserId, string Ownership, string[] Scopes, DateTimeOffset GrantExpiresAt,
    string ReturnUrl, DateTimeOffset ExpiresAt, string Status);
public sealed record MemberHandoffReceipt(Guid RequestId, Guid TargetUserId, string Ownership,
    string Status, DateTimeOffset ExpiresAt, string Delivery = "operator_stub");
public sealed record MemberHandoffDelivery(string To, string Subject, string Text);
