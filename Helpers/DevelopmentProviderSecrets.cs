namespace AZOA.WebAPI.Helpers;

/// <summary>Loads explicitly named development provider aliases without changing configured settings.</summary>
public static class DevelopmentProviderSecrets
{
    public static void Apply(ConfigurationManager configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment()) return;
        var path = Path.Combine(environment.ContentRootPath, ".env");
        if (!File.Exists(path)) return;
        var aliases = ReadAliases(File.ReadLines(path));
        var defaults = new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(configuration["Kyc:Identomat:CompanyKey"])
            && aliases.TryGetValue("IDENTOMAT_API_KEY", out var companyKey))
            defaults["Kyc:Identomat:CompanyKey"] = companyKey;
        if (string.IsNullOrWhiteSpace(configuration["Fiat:Test:SecretKey"])
            && aliases.TryGetValue("STRIPE_SECRET_API_KEY_DEV", out var stripeKey)
            && stripeKey.StartsWith("sk_test_", StringComparison.Ordinal))
            defaults["Fiat:Test:SecretKey"] = stripeKey;
        foreach (var alias in new[] { "Kyc__Identomat__ConfigId", "Kyc__Identomat__CallbackApiKey",
            "Kyc__Identomat__ReturnUrl", "Kyb__ConfigId", "Kyb__ReturnUrl", "Fiat__Test__WebhookSecret", "Fiat__Test__ConnectWebhookSecret" })
        {
            var setting = alias.Replace("__", ":", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(configuration[setting]) && aliases.TryGetValue(alias, out var value))
                defaults[setting] = value;
        }
        configuration.AddInMemoryCollection(defaults);
    }

    public static IReadOnlyDictionary<string, string> ReadAliases(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#')) continue;
            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;
            var name = trimmed[..separator].Trim();
            if (name is not ("IDENTOMAT_API_KEY" or "STRIPE_SECRET_API_KEY_DEV"
                or "Kyc__Identomat__ConfigId" or "Kyc__Identomat__CallbackApiKey" or "Kyc__Identomat__ReturnUrl"
                or "Kyb__ConfigId" or "Kyb__ReturnUrl" or "Fiat__Test__WebhookSecret"
                or "Fiat__Test__ConnectWebhookSecret")) continue;
            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            if (!string.IsNullOrWhiteSpace(value)) result[name] = value;
        }
        return result;
    }
}
