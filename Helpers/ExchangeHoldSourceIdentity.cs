using AZOA.WebAPI.Models.Blockchain;

namespace AZOA.WebAPI.Helpers;

/// <summary>Scopes an exchange source to the actual tenant API key and original request key.</summary>
public static class ExchangeHoldSourceIdentity
{
    public static string ForRequest(Guid tenant, Guid apiKey, string requestKey)
        => CanonicalJson.Hash(new { Domain = "exchange-hold-source-v1", Tenant = tenant, ApiKey = apiKey, RequestKey = requestKey });

    public static string ForIntent(ExchangeHoldSourceIntent intent)
        => ForRequest(intent.TenantUserId, intent.ApiKeyId, intent.RequestKey);
}
