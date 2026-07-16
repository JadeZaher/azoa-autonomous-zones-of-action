using System.Text.RegularExpressions;

namespace AZOA.WebAPI.Tests.Architecture;

public sealed partial class PackagingBoundaryTests
{
    [Fact]
    public void Web_api_host_is_explicitly_not_packable()
    {
        var project = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "AZOA.WebAPI.csproj"));

        Assert.Matches("<IsPackable>\\s*false\\s*</IsPackable>", project);
    }

    [Fact]
    public void Release_solution_configurations_never_select_debug()
    {
        var solution = File.ReadAllText(Path.Combine(ResolveRepoRoot(), "azoa.sln"));
        var debugMappings = ReleaseActiveConfigurationRegex().Matches(solution)
            .Select(match => match.Value)
            .ToArray();

        Assert.Empty(debugMappings);
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "azoa.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }

    [GeneratedRegex(@"\.Release\|[^\r\n]+\.ActiveCfg\s*=\s*Debug\|", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseActiveConfigurationRegex();
}
