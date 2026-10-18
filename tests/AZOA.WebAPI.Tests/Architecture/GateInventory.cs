using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
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

        public IReadOnlyList<string>? Strings(string name)
            => Value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
                ? property.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : null;
    }

    internal sealed record RouteSurface(string Controller, string Action, string Verb, string Template, MethodInfo Method)
    {
        /// <summary>Row key: verb, normalized template and controller action (overloads and multi-template actions stay distinct).</summary>
        public string Key => RouteKey(Verb, Template, Controller, Action);
    }

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

    /// <summary>Every public controller action method (verb-less ones included, so a <c>[Route]</c>-only all-verb action is visible).</summary>
    internal static IReadOnlyList<(Type Controller, MethodInfo Method)> Actions()
        => Api.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true } && typeof(ControllerBase).IsAssignableFrom(type)
                && type.GetCustomAttribute<NonControllerAttribute>() is null)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() is null)
                .Select(method => (type, method)))
            .ToArray();

    /// <summary>Every public controller action with its HTTP verb(s) and combined route template(s).</summary>
    internal static IReadOnlyList<RouteSurface> Routes()
        => Actions().SelectMany(item => item.Method.GetCustomAttributes().OfType<IActionHttpMethodProvider>()
                .SelectMany(provider => Templates(item.Method, provider)
                    .SelectMany(template => Prefixes(item.Controller).Select(prefix => Combine(prefix, template, item.Controller.Name)))
                    .SelectMany(route => provider.HttpMethods.Select(verb =>
                        new RouteSurface(ControllerName(item.Controller.Name), item.Method.Name, verb.ToUpperInvariant(), route, item.Method)))))
            .ToArray();

    /// <summary>The authorization an action actually carries: class and method <c>[Authorize]</c> policies ("authenticated" when no
    /// policy is named), plus "anonymous" for <c>[AllowAnonymous]</c>, or "none" when it carries neither.</summary>
    internal static IReadOnlyList<string> Policies(MethodInfo method)
    {
        var attributes = method.DeclaringType!.GetCustomAttributes(inherit: true).Concat(method.GetCustomAttributes(inherit: true)).ToArray();
        var policies = attributes.OfType<AuthorizeAttribute>()
            .Select(attribute => string.IsNullOrWhiteSpace(attribute.Policy) ? "authenticated" : attribute.Policy!)
            .Concat(attributes.OfType<AllowAnonymousAttribute>().Select(_ => "anonymous"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return policies.Length == 0 ? ["none"] : policies;
    }

    /// <summary>Row key from a verb, a template and a controller action.</summary>
    internal static string RouteKey(string? verb, string? template, string? controller, string? action)
        => $"{verb?.Trim().ToUpperInvariant()} /{NormalizeRoute(template)} ({ControllerName(controller)}.{action})";

    /// <summary>Splits a row route <c>"VERB /template"</c> into its verb and template.</summary>
    internal static (string? Verb, string Template) SplitRoute(string? route)
    {
        var text = (route ?? "").Trim();
        var space = text.IndexOf(' ');
        return space > 0 && text[..space].All(char.IsLetter) ? (text[..space], text[(space + 1)..]) : (null, text);
    }

    private static IEnumerable<string?> Prefixes(Type controller)
    {
        var templates = controller.GetCustomAttributes<RouteAttribute>(inherit: true).Select(route => (string?)route.Template).ToArray();
        return templates.Length == 0 ? [null] : templates;
    }

    private static IEnumerable<string?> Templates(MethodInfo method, IActionHttpMethodProvider provider)
    {
        if (provider is IRouteTemplateProvider { Template: not null } templated) return [templated.Template];
        var routes = method.GetCustomAttributes<RouteAttribute>().Select(route => (string?)route.Template).ToArray();
        return routes.Length == 0 ? [null] : routes;
    }

    /// <summary>Store contracts the gate classifies: every public interface named <c>*Store</c> or <c>*Ledger</c> in the API assembly,
    /// plus any concrete <c>Surreal*Store</c>/<c>*Ledger</c> class that implements none of them (X-5).</summary>
    internal static IReadOnlyList<Type> StoreContracts()
    {
        var interfaces = Api.GetTypes().Where(type => type is { IsInterface: true, IsPublic: true } && IsStoreName(type.Name)).ToArray();
        var concrete = Api.GetTypes().Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
            && type.Name.StartsWith("Surreal", StringComparison.Ordinal) && IsStoreName(type.Name)
            && !type.GetInterfaces().Any(interfaces.Contains)).ToArray();
        return interfaces.Concat(concrete).ToArray();
    }

    /// <summary>Owned-resource store methods: an ownership-named parameter, or an owned row in a parameter or the result.</summary>
    internal static IReadOnlyList<StoreSurface> OwnedStoreMethods()
        => StoreContracts()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName && IsOwnedResource(method))
                .Select(method => new StoreSurface(type.Name, method.Name, method)))
            .DistinctBy(surface => (surface.Store, surface.Method))
            .ToArray();

    /// <summary>Normalizes a controller name: <c>FooController</c> and <c>Foo</c> are the same key.</summary>
    internal static string ControllerName(string? name)
        => name is null ? "" : name.EndsWith("Controller", StringComparison.Ordinal) ? name[..^"Controller".Length] : name;

    /// <summary>Lowercase template without verb, leading slash or route constraints.</summary>
    internal static string NormalizeRoute(string? route)
    {
        var text = SplitRoute(route).Template;
        return ConstraintRegex().Replace(text.Trim().Trim('/').ToLowerInvariant(), "{$1}");
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

    private static bool IsStoreName(string name)
        => name.EndsWith("Store", StringComparison.Ordinal) || name.EndsWith("Ledger", StringComparison.Ordinal);

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
