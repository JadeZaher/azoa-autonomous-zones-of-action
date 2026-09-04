using System.Text.RegularExpressions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>
/// Source-level guards on the platform signing door. These assert on FILE CONTENT rather
/// than behaviour on purpose: the no-custody fallback denial reads the process-wide
/// <c>ASPNETCORE_ENVIRONMENT</c>, so exercising it would require mutating a global that
/// the parallel provider tests also read. See <c>Services/Custody/AGENTS.md</c>
/// §platform-key-derivation.
/// </summary>
public sealed partial class PlatformSigningDoorGuardTests
{
    private static readonly string[] ProvidersWithAPlatformFallback =
    [
        "Providers/Blockchain/Algorand/AlgorandProvider.cs",
        "Providers/Blockchain/Solana/SolanaProvider.cs",
        "Providers/Blockchain/Ethereum/EthereumProvider.cs",
    ];

    [Fact]
    public void No_custody_platform_fallback_stays_denied_in_production_and_staging()
    {
        var offenders = new List<string>();

        foreach (var relativePath in ProvidersWithAPlatformFallback)
        {
            var path = Path.Combine(ResolveRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                continue; // A provider that does not exist yet cannot regress.

            var source = File.ReadAllText(path);
            if (!source.Contains("SignWithFallbackPlatformKeyAsync", StringComparison.Ordinal))
                continue;

            if (!ProductionDenialRegex().IsMatch(source) || !StagingDenialRegex().IsMatch(source))
                offenders.Add(relativePath);
        }

        offenders.Should().BeEmpty(
            "a no-custody platform signing fallback must stay refused in Production and Staging — "
            + "there, a missing custody service is a DI regression, and signing outside the consent "
            + "gate must never be the silent outcome");
    }

    [Fact]
    public void Custody_platform_door_never_hardcodes_one_chains_derivation()
    {
        var path = Path.Combine(ResolveRepoRoot(), "Services", "Custody", "KeyCustodyService.cs");
        var source = File.ReadAllText(path);

        source.Should().NotContain("new AlgoAccount(",
            "the chokepoint must derive through IPlatformKeyDerivation, never by importing one "
            + "chain's account type inline");
        source.Should().Contain("_derivations.TryGet",
            "the platform door resolves the chain's rule from the registry");
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

    [GeneratedRegex(@"aspnetEnv\.Equals\(\s*""Production""", RegexOptions.IgnoreCase)]
    private static partial Regex ProductionDenialRegex();

    [GeneratedRegex(@"aspnetEnv\.Equals\(\s*""Staging""", RegexOptions.IgnoreCase)]
    private static partial Regex StagingDenialRegex();
}
