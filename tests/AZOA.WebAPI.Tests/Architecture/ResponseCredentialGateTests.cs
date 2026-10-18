using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>No statically typed controller response can serialize credential material (QA H5); see Architecture/AGENTS.md §Response credential gate.</summary>
public sealed partial class ResponseCredentialGateTests
{
    /// <summary>One-time reveals that ARE the point of their endpoint (shown once at creation, never stored in plaintext).</summary>
    private static readonly HashSet<string> OneTimeReveals = new(StringComparer.Ordinal)
    {
        // POST api/wallet/{id}/export: RecentFirstPartyLogin owner exporting their own platform wallet.
        "WalletExportResult.PrivateKey",
        "WalletExportResult.SeedPhrase",
    };

    [Fact]
    public void No_controller_response_type_exposes_a_credential_property()
    {
        var visited = new HashSet<Type>();
        var leaks = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (controller, method) in GateInventory.Actions())
            Walk(method.ReturnType, $"{controller.Name}.{method.Name}", visited, leaks);

        leaks.Where(leak => !OneTimeReveals.Contains(leak.Split(" via ")[0])).Should().BeEmpty(
            "password hashes, secrets and encrypted key material never leave the server; project to a response DTO or mark the property [JsonIgnore]");
    }

    private static void Walk(Type type, string origin, HashSet<Type> visited, SortedSet<string> leaks)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsArray)
        {
            Walk(type.GetElementType()!, origin, visited, leaks);
            return;
        }
        if (type.IsGenericType)
            foreach (var argument in type.GetGenericArguments())
                Walk(argument, origin, visited, leaks);
        if (type.Assembly != GateInventory.Api || type.IsEnum || !visited.Add(type))
            return;

        var properties = type.IsInterface
            ? type.GetInterfaces().Append(type).SelectMany(contract => contract.GetProperties())
            : type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
                continue;
            if (CredentialName().IsMatch(property.Name))
                leaks.Add($"{type.Name}.{property.Name} via {origin}");
            Walk(property.PropertyType, origin, visited, leaks);
        }
    }

    [GeneratedRegex("^(Password|PasswordHash|.*Secret|.*SecretKey|Encrypted.*|PrivateKey|SeedPhrase|Mnemonic|.*KeyHash|.*TokenHash)$", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialName();
}
