using System.Security.Cryptography;

namespace AZOA.WebAPI.Helpers.Deployments;

/// <summary>Domain-separated stable record identities; never signing-key material.</summary>
public static class DeploymentIdentity
{
    public static Guid GuidFor(string domain, params string[] parts)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { domain, parts });
        return new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)).AsSpan(0, 16));
    }

    public static string Operation(Guid actor, Guid? tenant, string requestKey)
        => GuidFor("azoa-deployment/2", actor.ToString("N"), tenant?.ToString("N") ?? "", requestKey).ToString("N");
}
