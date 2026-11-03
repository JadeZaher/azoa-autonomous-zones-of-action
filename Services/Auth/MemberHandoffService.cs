using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores.Surreal;
using AZOA.WebAPI.Services.Consent;
using AZOA.WebAPI.Services.Signing;
using Handoff = AZOA.WebAPI.Persistence.SurrealDb.Models.MemberHandoff;
using Invite = AZOA.WebAPI.Persistence.SurrealDb.Models.WalletAuthClaimToken;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Member-owned account handoff and explicit consent; see AGENTS.md §member-handoff.</summary>
public sealed class MemberHandoffService(SurrealMemberHandoffStore store, IUserStore users,
    SigningAdmissionAuthorityFactory authorities, ConsentLifecycleService lifecycle,
    MemberHandoffOptions options, ClaimInviteOptions invites, IOutboundEmailSender delivery, IWalletAuthClaimTokenStore claimTokens)
{
    public static IReadOnlyList<string> RequestedScopes { get; } = Array.AsReadOnly(new[] { "kyc:read", "kyc:submit", "units:issue", "swap:sign", "transfer:sign" });
    private static string Link(Guid id) => "user:" + id.ToString("N");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AZOAResult<MemberHandoffReceipt>> IssueAsync(ClaimsPrincipal principal, string external,
        MemberHandoffRequest request, Guid? resendId, CancellationToken ct)
    {
        if (!options.Enabled) return Failure<MemberHandoffReceipt>("Member account handoff is not available.", "PAYMENTS_NOT_CONFIGURED");
        if (string.IsNullOrWhiteSpace(external) || external.Length > 128 || external.Any(char.IsControl)
            || !ValidEmail(request.DeliveryEmail) || !options.AllowsReturn(request.ReturnUrl))
            return Failure<MemberHandoffReceipt>("A valid delivery address and configured return destination are required.", AzoaErrorCodes.InvalidRequest);
        var captured = await authorities.CaptureCredentialAsync(principal, ct);
        if (captured.IsError || captured.Result is null || captured.Result.TenantId is not null)
            return Failure<MemberHandoffReceipt>("Current tenant authority is required.", AzoaErrorCodes.Forbidden);
        var tenantId = captured.Result.ActorUserId;
        var bound = await authorities.BindConsentAsync(captured.Result, [AzoaScopes.TenantProvision], ct);
        if (bound.IsError || bound.Result is null)
            return Failure<MemberHandoffReceipt>("Current tenant authority is required.", AzoaErrorCodes.Forbidden);
        external = external.Trim();
        var memberId = TenantChildCorrelation.DeterministicUserId(tenantId, external);
        var loaded = await users.GetByIdAsync(memberId, ct);
        if (loaded.IsError || loaded.Result is not { IsActive: true } member || member.ExternalUserId != external
            || !TenantChildCorrelation.IsCorrelated(member, tenantId))
            return Failure<MemberHandoffReceipt>("No member for that external user id.", AzoaErrorCodes.NotFound);
        var now = DateTimeOffset.UtcNow;
        var email = request.DeliveryEmail.Trim();
        var emailDigest = OpaqueTokenDigest.Compute(email);
        Handoff? previous = null;
        MemberHandoffDescriptor descriptor;
        if (resendId is { } id)
        {
            previous = await store.GetAsync(id, ct);
            if (previous is null || previous.TenantId != Link(tenantId) || previous.TargetUserId != Link(memberId)
                || previous.ExternalSubject != external)
                return Failure<MemberHandoffReceipt>("No handoff for that member.", AzoaErrorCodes.NotFound);
            descriptor = ReadDescriptor(previous);
            if (previous.Status != "pending" || previous.ExpiresAt <= now || descriptor.ReturnUrl != request.ReturnUrl
                || previous.DeliveryEmailDigest != emailDigest || (descriptor.Ownership == "custodial") != (member.OwnerTenantId is not null))
                return Failure<MemberHandoffReceipt>("This handoff changed or expired. Start a new handoff.", AzoaErrorCodes.Conflict);
            if (now - previous.UpdatedAt < TimeSpan.FromMinutes(1))
                return Failure<MemberHandoffReceipt>("Wait a minute before resending.", "RAIL_BUSY");
        }
        else
        {
            var tenant = await users.GetByIdAsync(tenantId, ct);
            descriptor = new(Guid.NewGuid(), tenantId, tenant.Result?.Username ?? "Your platform", memberId,
                member.OwnerTenantId is null ? "claimed" : "custodial", [.. RequestedScopes], now.AddMonths(12),
                request.ReturnUrl, now.Add(invites.Lifetime));
        }
        var token = descriptor.Ownership == "custodial" ? Crypto.GenerateNonce() : null;
        var hash = token is null ? null : OpaqueTokenDigest.Compute(token);
        var fragment = "requestId=" + descriptor.RequestId.ToString("D") + "&targetUserId=" + memberId.ToString("D");
        if (token is not null) fragment += "&token=" + Uri.EscapeDataString(token);
        var url = options.HostedBaseUrl.TrimEnd('/') + (token is null ? "/consent#" : "/claim#") + fragment;
        var message = new MemberHandoffDelivery(email, "Set up your account",
            "Review your account and permissions. This link expires at " + descriptor.ExpiresAt.ToString("O") + ".\n" + url);
        var row = new Handoff
        {
            Id = descriptor.RequestId.ToString("N"), TenantId = Link(tenantId), TargetUserId = Link(memberId),
            ExternalSubject = external, Descriptor = JsonSerializer.Serialize(descriptor, Json),
            DeliveryEmailDigest = emailDigest, ProtectedDelivery = delivery.Prepare(message),
            TokenDigest = hash, ExpiresAt = descriptor.ExpiresAt, UpdatedAt = now, Version = (previous?.Version ?? -1) + 1,
        };
        var invite = token is null ? null : new Invite
        {
            Id = Guid.NewGuid().ToString("N"), Token = hash!, TargetUserId = Link(memberId), TenantId = Link(tenantId),
            ExpiresAt = descriptor.ExpiresAt, CreatedAt = now,
        };
        await store.SaveDeliveryAsync(row, previous, invite, memberId,
            member.OwnerTenantId is { } owner ? Link(owner) : null,
            member.AuthNotBefore is { } watermark ? new DateTimeOffset(DateTime.SpecifyKind(watermark, DateTimeKind.Utc)) : null,
            bound.Result, ct);
        return AZOAResult<MemberHandoffReceipt>.Success(Receipt(row));
    }

    public async Task<AZOAResult<MemberHandoffReceipt>> TenantReadAsync(ClaimsPrincipal principal, string external, Guid id, CancellationToken ct)
    {
        var row = await store.GetAsync(id, ct);
        return row is not null && AzoaClaims.TryGetSubjectId(principal, out var actor) && row.TenantId == Link(actor)
            && row.ExternalSubject == external
            ? AZOAResult<MemberHandoffReceipt>.Success(Receipt(row))
            : Failure<MemberHandoffReceipt>("No handoff for that member.", AzoaErrorCodes.NotFound);
    }

    public async Task<AZOAResult<MemberHandoffView>> PreviewAsync(MemberHandoffPreview request, CancellationToken ct)
    {
        var row = await store.GetAsync(request.RequestId, ct);
        if (row is null || row.Status != "pending" || row.ExpiresAt <= DateTimeOffset.UtcNow
            || row.TokenDigest is null || string.IsNullOrWhiteSpace(request.ClaimToken)
            || !OpaqueTokenDigest.Matches(row.TokenDigest, request.ClaimToken))
            return Failure<MemberHandoffView>("This invitation is invalid or expired.", AzoaErrorCodes.NotFound);
        var invite = await claimTokens.GetByTokenHashAsync(row.TokenDigest, ct);
        return invite.Result is { } live && live.IsLiveAt(DateTime.UtcNow)
            ? AZOAResult<MemberHandoffView>.Success(View(row))
            : Failure<MemberHandoffView>("This invitation is invalid or expired.", AzoaErrorCodes.NotFound);
    }

    public async Task<AZOAResult<MemberHandoffView>> ReadAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        var row = await OwnedAsync(principal, id, ct);
        return row is null ? Failure<MemberHandoffView>("No handoff for this account.", AzoaErrorCodes.NotFound)
            : AZOAResult<MemberHandoffView>.Success(View(row));
    }

    public async Task<AZOAResult<MemberHandoffView>> DecideAsync(ClaimsPrincipal principal, Guid id, bool approve, CancellationToken ct)
    {
        var row = await OwnedAsync(principal, id, ct);
        if (row is null) return Failure<MemberHandoffView>("No handoff for this account.", AzoaErrorCodes.NotFound);
        if (!approve && row.Status == "declined") return AZOAResult<MemberHandoffView>.Success(View(row));
        if (approve && row.Status == "approved") return AZOAResult<MemberHandoffView>.Success(View(row));
        if (!approve && row.Status == "approving")
            return await WithdrawApprovalAsync(principal, row, ct);
        if (approve && row.Status == "pending" && row.ExpiresAt <= DateTimeOffset.UtcNow)
            return Failure<MemberHandoffView>("This invitation expired. Return to your platform for a fresh request.", "HANDOFF_EXPIRED");
        if (row.Status == "declined" || (!approve && row.Status != "pending"))
            return Failure<MemberHandoffView>("This request is no longer awaiting a decision.", AzoaErrorCodes.Conflict);
        var descriptor = ReadDescriptor(row);
        var member = await users.GetByIdAsync(descriptor.TargetUserId, ct);
        if (member.Result is not { OwnerTenantId: null, AuthNotBefore: not null })
            return Failure<MemberHandoffView>("Claim your account before granting permission.", AzoaErrorCodes.Forbidden);
        if (row.Status == "pending")
        {
            if (!await store.TransitionAsync(row, "pending", approve ? "approving" : "declined", ct))
                return Failure<MemberHandoffView>("The request changed. Refresh and retry.", AzoaErrorCodes.Conflict);
            row = (await store.GetAsync(id, ct))!;
        }
        if (!approve) return AZOAResult<MemberHandoffView>.Success(View(row));
        var granted = await lifecycle.GrantHandoffAsync(principal, new ConsentLifecycleCommand(descriptor.RequestId,
            descriptor.TenantId, descriptor.Scopes, GrantOrigin.UserExplicit, null, descriptor.GrantExpiresAt), row, ct);
        if (granted.IsError || granted.Result is null)
            return row.ExpiresAt <= DateTimeOffset.UtcNow
                ? Failure<MemberHandoffView>("This invitation expired. Resolve the earlier approval with Decline, or return to your platform for a fresh request.", "HANDOFF_EXPIRED")
                : Failure<MemberHandoffView>("Permission could not be confirmed. Retry this same request.", AzoaErrorCodes.Conflict);
        await store.TransitionAsync(row, "approving", "approved", ct);
        row = (await store.GetAsync(id, ct))!;
        return AZOAResult<MemberHandoffView>.Success(View(row));
    }

    private async Task<AZOAResult<MemberHandoffView>> WithdrawApprovalAsync(ClaimsPrincipal principal, Handoff row, CancellationToken ct)
    {
        var descriptor = ReadDescriptor(row);
        var command = new ConsentLifecycleCommand(descriptor.RequestId, descriptor.TenantId, descriptor.Scopes,
            GrantOrigin.UserExplicit, null, descriptor.GrantExpiresAt);
        var withdrawal = await lifecycle.WithdrawAsync(principal, command, ct);
        if (withdrawal.IsError || withdrawal.Result is null)
            return Failure<MemberHandoffView>("The earlier permission could not be resolved. Retry this request.", AzoaErrorCodes.Conflict);
        if (withdrawal.Result.Receipt is { } admitted)
        {
            var revoked = await lifecycle.RevokeAsync(principal, admitted.GrantId, ct);
            if (revoked.IsError || revoked.Result is null)
                return Failure<MemberHandoffView>("The earlier permission needs to be revoked before declining.", AzoaErrorCodes.Conflict);
        }
        else if (!withdrawal.Result.Withdrawn)
            return Failure<MemberHandoffView>("The earlier permission could not be resolved. Retry this request.", AzoaErrorCodes.Conflict);
        row = (await store.GetAsync(descriptor.RequestId, ct))!;
        if (row.Status is "approving" or "approved")
            await store.TransitionAsync(row, row.Status, "declined", ct);
        row = (await store.GetAsync(descriptor.RequestId, ct))!;
        return AZOAResult<MemberHandoffView>.Success(View(row));
    }

    public async Task<object[]> DeliveriesAsync(CancellationToken ct)
        => (await store.ListAsync(ct)).Select(row => (object)new
        { requestId = Guid.Parse(SurrealRecordGuid.BareId(row.Id)), row.Status, row.ExpiresAt, row.UpdatedAt }).ToArray();

    public async Task<MemberHandoffDelivery?> DeliveryAsync(Guid id, CancellationToken ct)
    {
        var row = await store.GetAsync(id, ct);
        return row is null || row.Status != "pending" || row.ExpiresAt <= DateTimeOffset.UtcNow ? null
            : delivery.Read(row.ProtectedDelivery);
    }

    private async Task<Handoff?> OwnedAsync(ClaimsPrincipal principal, Guid id, CancellationToken ct)
    {
        if (!AzoaClaims.TryGetSubjectId(principal, out var actor)) return null;
        var row = await store.GetAsync(id, ct);
        return row?.TargetUserId == Link(actor) ? row : null;
    }

    private static MemberHandoffDescriptor ReadDescriptor(Handoff row)
        => JsonSerializer.Deserialize<MemberHandoffDescriptor>(row.Descriptor, Json)
            ?? throw new InvalidOperationException("Stored handoff descriptor is unreadable.");
    private static MemberHandoffReceipt Receipt(Handoff row)
    {
        var d = ReadDescriptor(row);
        return new(d.RequestId, d.TargetUserId, d.Ownership, row.Status, row.ExpiresAt);
    }
    private static MemberHandoffView View(Handoff row)
    {
        var d = ReadDescriptor(row);
        return new(d.RequestId, d.TenantId, d.TenantName, d.TargetUserId, d.Ownership, d.Scopes,
            d.GrantExpiresAt, d.ReturnUrl, d.ExpiresAt, row.Status);
    }
    private static AZOAResult<T> Failure<T>(string message, string code) => AZOAResult<T>.FailureWithCode(message, code);
    public static bool ValidEmail(string? value)
        => value is { Length: > 3 and <= 254 } && !value.Any(char.IsControl)
            && MailAddress.TryCreate(value, out var address) && address.Address == value.Trim();
}
