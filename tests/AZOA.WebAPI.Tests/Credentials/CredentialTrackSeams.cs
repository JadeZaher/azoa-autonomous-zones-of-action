using System.Reflection;

namespace AZOA.WebAPI.Tests.Credentials;

internal static class CredentialTrackSeams
{
    internal const string MissingSeamMessage = "Credential issuance must execute the durable recipient-aware lifecycle.";
    internal const string MissingIssuerMessage = "Confirmed issuance must retain the authenticated issuer separately from the holder.";

    internal static PropertyInfo? WritableIssuerPropertyOn(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(property => property.CanWrite
                && (property.PropertyType == typeof(Guid) || property.PropertyType == typeof(Guid?))
                && (property.Name.Contains("issuer", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("issuedby", StringComparison.OrdinalIgnoreCase)));
}
