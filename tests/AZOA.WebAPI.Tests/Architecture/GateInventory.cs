using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Loads the per-lane route and store inventories and reflects the live surface they classify; see Architecture/AGENTS.md §Gate inventories.</summary>
internal static partial class GateInventory
{
    internal static readonly Assembly Api = typeof(AZOA.WebAPI.Controllers.DeploymentsController).Assembly;

    internal sealed record Row(string File, JsonElement Value)
    {
        public string? Text(string name)
            => Value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    internal sealed record RouteSurface(string Controller, string Action, string Verb, string Template, MethodInfo Method);

    internal sealed record StoreSurface(string Store, string Method, MethodInfo Info);

    /// <summary>Every row of every <c>Inventories/{kind}/*.json</c> file (top-level array, or an object holding one array).</summary>
    internal static IReadOnlyList<Row> Load(string kind)
    {
        var directory = Path.Combine(RepoRoot(), "tests", "AZOA.WebAPI.Tests", "Architecture", "Inventories", kind);
        if (!Directory.Exists(directory)) return [];
        var rows = new List<Row>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array ? root
                : root.EnumerateObject().Select(property => property.Value).Single(value => value.ValueKind == JsonValueKind.Array);
            rows.AddRange(array.EnumerateArray().Select(item => new Row(Path.GetFileName(path), item.Clone())));
        }
        return rows;
    }

    /// <summary>Every public controller action with its HTTP verb and combined route template.</summary>
    internal static IReadOnlyList<RouteSurface> Routes()
        => Api.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()
                    .SelectMany(attribute => Templates(method, attribute)
                        .SelectMany(template => Prefixes(type).Select(prefix => Combine(prefix, template, type.Name)))
                        .SelectMany(route => attribute.HttpMethods.Select(verb => new RouteSurface(ControllerName(type.Name), method.Name, verb, route, method))))))
            .ToArray();

    private static IEnumerable<string?> Prefixes(Type controller)
    {
        var templates = controller.GetCustomAttributes<RouteAttribute>(inherit: true).Select(route => (string?)route.Template).ToArray();
        return templates.Length == 0 ? [null] : templates;
    }

    private static IEnumerable<string?> Templates(MethodInfo method, HttpMethodAttribute attribute)
    {
        if (attribute.Template is not null) return [attribute.Template];
        var routes = method.GetCustomAttributes<RouteAttribute>().Select(route => (string?)route.Template).ToArray();
        return routes.Length == 0 ? [null] : routes;
    }

    /// <summary>Owned-resource store methods: an ownership-named parameter, or an owned row in a parameter or the result.</summary>
    internal static IReadOnlyList<StoreSurface> OwnedStoreMethods()
    {
        var interfaces = Api.GetTypes().Where(type => type is { IsInterface: true, IsPublic: true }
            && type.Namespace == "AZOA.WebAPI.Interfaces.Stores").ToArray();
        var concrete = Api.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
            && type.Name.StartsWith("Surreal", StringComparison.Ordinal) && type.Name.EndsWith("Store", StringComparison.Ordinal)
            && !type.GetInterfaces().Any(contract => contract.Namespace == "AZOA.WebAPI.Interfaces.Stores")).ToArray();
        return interfaces.Concat(concrete)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && IsOwnedResource(method))
                .Select(method => new StoreSurface(type.Name, method.Name, method)))
            .DistinctBy(surface => (surface.Store, surface.Method))
            .ToArray();
    }

    /// <summary>Normalizes a controller name: <c>FooController</c> and <c>Foo</c> are the same key.</summary>
    internal static string ControllerName(string? name)
        => name is null ? "" : name.EndsWith("Controller", StringComparison.Ordinal) ? name[..^"Controller".Length] : name;

    /// <summary>Lowercase template without verb, leading slash or route constraints.</summary>
    internal static string NormalizeRoute(string? route)
    {
        var text = (route ?? "").Trim();
        var space = text.IndexOf(' ');
        if (space > 0 && text[..space].All(char.IsLetter)) text = text[(space + 1)..].Trim();
        return ConstraintRegex().Replace(text.Trim('/').ToLowerInvariant(), "{$1}");
    }

    internal static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }

    private static readonly HashSet<string> OwnershipProperties = new(StringComparer.Ordinal)
    {
        "UserId", "OwnerUserId", "OwnerId", "TenantId", "ActorUserId", "GrantorUserId", "AvatarId", "OwnerTenantId", "ActingTenantId",
    };

    private static bool IsOwnedResource(MethodInfo method)
        => method.GetParameters().Any(parameter => OwnershipParameterRegex().IsMatch(parameter.Name ?? "")
                || HasOwnership(parameter.ParameterType))
            || HasOwnership(method.ReturnType);

    private static bool HasOwnership(Type type)
    {
        var unwrapped = Unwrap(type);
        return unwrapped.Namespace?.StartsWith("AZOA.WebAPI", StringComparison.Ordinal) == true
            && unwrapped.GetProperties(BindingFlags.Instance | BindingFlags.Public).Any(property => OwnershipProperties.Contains(property.Name));
    }

    private static Type Unwrap(Type type)
    {
        while (true)
        {
            if (type.IsArray) { type = type.GetElementType()!; continue; }
            if (Nullable.GetUnderlyingType(type) is { } inner) { type = inner; continue; }
            if (!type.IsGenericType) return type;
            var arguments = type.GetGenericArguments();
            if (arguments.Length != 1) return type;
            type = arguments[0];
        }
    }

    private static string Combine(string? prefix, string? template, string controller)
    {
        var combined = template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal))
            ? template.TrimStart('~')
            : string.Join('/', new[] { prefix, template }.Where(part => !string.IsNullOrEmpty(part)));
        return NormalizeRoute(combined.Replace("[controller]", ControllerName(controller), StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(:[^}]*)?\??\}")]
    private static partial Regex ConstraintRegex();

    [GeneratedRegex(@"^(userId|ownerId|ownerUserId|actor|actorId|actorUserId|owner|tenant|tenantId|avatarId|grantorUserId|grantorId|requesterId|holderId|issuerId|callerId|callerUserId)$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnershipParameterRegex();
}
