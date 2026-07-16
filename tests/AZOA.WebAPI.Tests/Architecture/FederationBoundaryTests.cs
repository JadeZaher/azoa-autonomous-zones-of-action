using System.Text.RegularExpressions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Protects the deferred Holochain runtime boundary; see <c>Architecture/AGENTS.md</c>.</summary>
public sealed partial class FederationBoundaryTests
{
    private static readonly string[] ProductionRoots =
    {
        "Providers", "Services", "Managers", "Controllers", "Middleware",
        "Extensions", "Observability", "Persistence", "Core", "Helpers", "Mcp",
    };

    [Fact]
    public void NodeProject_DoesNotReferenceHolochainRuntimeOrVendoredPriorArt()
    {
        var project = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "AZOA.WebAPI.csproj"));

        var forbiddenReferences = ProjectReferenceRegex()
            .Matches(project)
            .Select(match => match.Value.Trim())
            .ToList();

        Assert.Empty(forbiddenReferences);
    }

    [Fact]
    public void NodeProject_DoesNotReferenceAnyDeferredCommonsPackageBeforeActivationEvidence()
    {
        var project = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "AZOA.WebAPI.csproj"));

        var forbiddenReferences = DeferredCommonsReferenceRegex()
            .Matches(project)
            .Select(match => match.Value.Trim())
            .ToList();

        Assert.Empty(forbiddenReferences);
    }

    [Fact]
    public void ProductionCode_DoesNotReferenceHolochainImplementationNamespaces()
    {
        var repoRoot = ResolveRepoRoot();
        var violations = ProductionRoots
            .Select(root => Path.Combine(repoRoot, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => !HasPathSegment(file, "bin") && !HasPathSegment(file, "obj"))
            .SelectMany(file => ImplementationReferenceRegex()
                .Matches(File.ReadAllText(file))
                .Select(match => $"{Path.GetRelativePath(repoRoot, file)}: {match.Value}"))
            .ToList();

        Assert.Empty(violations);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AZOA.WebAPI.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }

    private static bool HasPathSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"<(?:PackageReference|ProjectReference)\b[^>]*(?:Holochain|HoloNET)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectReferenceRegex();

    [GeneratedRegex(@"<(?:PackageReference|ProjectReference)\b[^>]*(?:Azoa\.Commons(?:\.[A-Za-z0-9_.-]+)?|Azoa\.Holochain(?:\.[A-Za-z0-9_.-]+)?)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DeferredCommonsReferenceRegex();

    [GeneratedRegex(@"\b(?:NextGenSoftware\.Holochain|Azoa\.Holochain(?:\.[A-Za-z0-9_.-]+)?|Holochain(?:\.[A-Za-z0-9_.-]+)?)\b")]
    private static partial Regex ImplementationReferenceRegex();
}
