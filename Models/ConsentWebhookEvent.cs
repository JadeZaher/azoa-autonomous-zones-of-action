namespace AZOA.WebAPI.Models;

/// <summary>Consent lifecycle facts recorded atomically for notification delivery.</summary>
public enum ConsentWebhookEventType
{
    Granted = 0,
    Revoked = 1,
    Expired = 2,
}
