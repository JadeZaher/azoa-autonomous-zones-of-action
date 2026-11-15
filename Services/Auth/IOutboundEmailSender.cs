using AZOA.WebAPI.Models;
using Microsoft.AspNetCore.DataProtection;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>Prepares protected email delivery for the transactional operator outbox.</summary>
public interface IOutboundEmailSender
{
    string Prepare(MemberHandoffDelivery message);
    MemberHandoffDelivery Read(string protectedMessage);
}

/// <summary>Records email for operator delivery; sends no external message.</summary>
public sealed class ProtectedStubEmailSender(IDataProtectionProvider provider) : IOutboundEmailSender
{
    private readonly IDataProtector _protector = provider.CreateProtector("azoa.member-handoff.delivery.v1");
    public string Prepare(MemberHandoffDelivery message)
        => _protector.Protect(System.Text.Json.JsonSerializer.Serialize(message));
    public MemberHandoffDelivery Read(string protectedMessage)
        => System.Text.Json.JsonSerializer.Deserialize<MemberHandoffDelivery>(_protector.Unprotect(protectedMessage))
            ?? throw new InvalidOperationException("The protected delivery is unreadable.");
}
