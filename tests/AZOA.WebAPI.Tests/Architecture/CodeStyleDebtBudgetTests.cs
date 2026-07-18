using System.Text.RegularExpressions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Ratcheting source debt ceilings; see <c>Architecture/AGENTS.md</c>.</summary>
public sealed partial class CodeStyleDebtBudgetTests
{
    private const int ProductionCatchAllCeiling = 205;
    // Includes the three parameterized NodeFeeSettlement lease/admission/
    // terminal CAS statements, waived only until 2026-08-31; see
    // Providers/Stores/Surreal/AGENTS.md.
    private const int ProductionLiteralMutationCeiling = 80;

    private static readonly string[] ProductionRoots =
    {
        "Providers", "Services", "Managers", "Controllers", "Middleware",
        "Extensions", "Observability", "Persistence", "Core", "Helpers", "Mcp",
    };

    private static readonly IReadOnlyDictionary<string, int> RawQueryCeilings =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SurrealApiKeyStore.cs"] = 8,
            ["SurrealAvatarStore.cs"] = 6,
            ["SurrealBridgeStore.cs"] = 15,
            ["SurrealConsentAuditStore.cs"] = 2,
            ["SurrealConsentGrantStore.cs"] = 6,
            ["SurrealConsentWebhookOutboxStore.cs"] = 5,
            ["SurrealDataMigrationLedgerStore.cs"] = 3,
            ["SurrealEcosystemStore.cs"] = 3,
            ["SurrealHolonStore.cs"] = 4,
            ["SurrealHolonTypeRegistryStore.cs"] = 4,
            ["SurrealKycStore.cs"] = 6,
            ["SurrealNftStore.cs"] = 2,
            ["SurrealNodeFeeScheduleStore.cs"] = 6,
            // Lease CAS plus parameterized multi-table parent/settlement
            // admission, accepted-group receipt, and terminal transactions.
            // The receipt is one waiver but SurrealQuery.Of accepts only one
            // statement, so its BEGIN/LET/UPDATE/CREATE/SELECT/COMMIT pieces
            // must compose through Combine; waiver expires 2026-08-31.
            ["SurrealNodeFeeSettlementStore.cs"] = 26,
            ["SurrealNodeGovernanceStore.cs"] = 6,
            ["SurrealNodeTreasuryStore.cs"] = 6,
            ["SurrealNodeTransparencyStore.cs"] = 2,
            ["SurrealQuestAccessRequestStore.cs"] = 9,
            ["SurrealQuestNodeExecutionStore.cs"] = 8,
            ["SurrealQuestRunStore.cs"] = 13,
            ["SurrealQuestStore.cs"] = 18,
            ["SurrealQuestTemplateStore.cs"] = 2,
            ["SurrealQuestWebhookOutboxStore.cs"] = 5,
            ["SurrealStarStore.cs"] = 1,
            ["SurrealWalletAuthChallengeStore.cs"] = 5,
            ["SurrealWalletAuthClaimTokenStore.cs"] = 3,
            ["SurrealWebhookRegistrationStore.cs"] = 2,
        };

    private static readonly IReadOnlyDictionary<string, int> MutationCeilings =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SurrealApiKeyStore.cs|CREATE"] = 1,
            ["SurrealApiKeyStore.cs|DELETE"] = 1,
            ["SurrealApiKeyStore.cs|UPDATE"] = 3,
            ["SurrealAvatarStore.cs|DELETE"] = 1,
            ["SurrealBridgeStore.cs|CREATE"] = 1,
            ["SurrealBridgeStore.cs|UPDATE"] = 2,
            ["SurrealConsentAuditStore.cs|CREATE"] = 1,
            ["SurrealConsentGrantStore.cs|UPDATE"] = 1,
            ["SurrealConsentWebhookOutboxStore.cs|CREATE"] = 1,
            ["SurrealConsentWebhookOutboxStore.cs|UPDATE"] = 3,
            ["SurrealDataMigrationLedgerStore.cs|CREATE"] = 1,
            ["SurrealEcosystemStore.cs|DELETE"] = 1,
            ["SurrealHolonStore.cs|DELETE"] = 1,
            ["SurrealHolonStore.cs|UPDATE ONLY"] = 3,
            ["SurrealHolonTypeRegistryStore.cs|DELETE"] = 1,
            ["SurrealKycStore.cs|UPSERT"] = 2,
            ["SurrealNftStore.cs|UPSERT"] = 2,
            ["SurrealNodeFeeScheduleStore.cs|CREATE"] = 1,
            ["SurrealNodeFeeScheduleStore.cs|UPSERT"] = 1,
            ["SurrealNodeFeeSettlementStore.cs|UPDATE ONLY"] = 3,
            ["SurrealNodeGovernanceStore.cs|CREATE"] = 1,
            ["SurrealNodeGovernanceStore.cs|UPSERT"] = 1,
            ["SurrealNodeTreasuryStore.cs|CREATE"] = 1,
            ["SurrealNodeTreasuryStore.cs|UPSERT"] = 1,
            ["SurrealQuestAccessRequestStore.cs|CREATE"] = 1,
            ["SurrealQuestAccessRequestStore.cs|UPSERT"] = 1,
            ["SurrealQuestNodeExecutionStore.cs|CREATE"] = 1,
            ["SurrealQuestNodeExecutionStore.cs|UPDATE"] = 1,
            ["SurrealQuestNodeExecutionStore.cs|UPSERT"] = 1,
            ["SurrealQuestRunStore.cs|CREATE"] = 2,
            ["SurrealQuestRunStore.cs|UPDATE"] = 1,
            ["SurrealQuestRunStore.cs|UPSERT"] = 1,
            ["SurrealQuestStore.cs|DELETE"] = 6,
            ["SurrealQuestStore.cs|UPDATE"] = 2,
            ["SurrealQuestStore.cs|UPSERT"] = 5,
            ["SurrealQuestWebhookOutboxStore.cs|CREATE"] = 1,
            ["SurrealQuestWebhookOutboxStore.cs|UPDATE"] = 3,
            ["SurrealWalletAuthChallengeStore.cs|CREATE"] = 1,
            ["SurrealWalletAuthChallengeStore.cs|UPDATE"] = 1,
            ["SurrealWalletAuthClaimTokenStore.cs|CREATE"] = 1,
            ["SurrealWalletAuthClaimTokenStore.cs|UPDATE"] = 1,
            ["SurrealWebhookRegistrationStore.cs|UPDATE"] = 1,
            ["Services/Sagas/SurrealSagaStore.cs|CREATE"] = 1,
            ["Services/Sagas/SurrealSagaStore.cs|UPDATE"] = 13,
        };

    private static readonly IReadOnlyDictionary<string, int> DynamicQueryCeilings =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Providers/Stores/Surreal/SurrealBridgeStore.cs"] = 3,
            ["Mcp/Tools/VectorSearchTool.cs"] = 1,
        };

    private static readonly IReadOnlyDictionary<string, int> CatchAllCeilings =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SurrealAvatarStore.cs"] = 7,
            ["SurrealBlockchainOperationStore.cs"] = 4,
            ["SurrealConsentAuditStore.cs"] = 2,
            ["SurrealConsentGrantStore.cs"] = 7,
            ["SurrealConsentWebhookOutboxStore.cs"] = 5,
            ["SurrealEcosystemStore.cs"] = 6,
            ["SurrealHolonTypeRegistryStore.cs"] = 4,
            ["SurrealKycStore.cs"] = 7,
            ["SurrealNftStore.cs"] = 13,
            ["SurrealQuestAccessRequestStore.cs"] = 6,
            ["SurrealQuestNodeExecutionStore.cs"] = 6,
            ["SurrealQuestRunStore.cs"] = 7,
            ["SurrealQuestStore.cs"] = 12,
            ["SurrealQuestWebhookOutboxStore.cs"] = 5,
            ["SurrealStarStore.cs"] = 5,
            ["SurrealWalletAuthChallengeStore.cs"] = 5,
            ["SurrealWalletAuthClaimTokenStore.cs"] = 3,
            ["SurrealWalletStore.cs"] = 5,
            ["SurrealWebhookRegistrationStore.cs"] = 3,
        };

    [Fact]
    public void Production_source_debt_does_not_exceed_checked_in_ceiling()
    {
        var repoRoot = ResolveRepoRoot();
        var storeRoot = Path.Combine(repoRoot, "Providers", "Stores", "Surreal");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(storeRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(storeRoot, file).Replace('\\', '/');
            var source = File.ReadAllText(file);
            CheckCeiling(relative, "raw query", RawQueryRegex().Matches(source).Count,
                RawQueryCeilings, violations);
            CheckCeiling(relative, "catch (Exception)", CatchAllRegex().Matches(source).Count,
                CatchAllCeilings, violations);

        }

        var productionFiles = ProductionRoots
            .Select(root => Path.Combine(repoRoot, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => !HasPathSegment(file, "bin")
                && !HasPathSegment(file, "obj")
                && !HasPathSegment(file, "Generated"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var catchAllCount = 0;
        var literalMutationCount = 0;
        foreach (var file in productionFiles)
        {
            var repoRelative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            var inventoryFile = file.StartsWith(storeRoot, StringComparison.OrdinalIgnoreCase)
                ? Path.GetRelativePath(storeRoot, file).Replace('\\', '/')
                : repoRelative;
            var source = File.ReadAllText(file);
            catchAllCount += CatchAllRegex().Matches(source).Count;

            var mutationMatches = RawMutationRegex().Matches(source);
            literalMutationCount += mutationMatches.Count;
            foreach (var group in mutationMatches
                         .Select(match => match.Groups["verb"].Value.ToUpperInvariant())
                         .GroupBy(verb => verb, StringComparer.Ordinal))
            {
                CheckCeiling($"{inventoryFile}|{group.Key}", "raw mutation", group.Count(),
                    MutationCeilings, violations);
            }

            CheckCeiling(repoRelative, "dynamic raw query", DynamicSqlQueryRegex().Matches(source).Count,
                DynamicQueryCeilings, violations);
        }

        if (catchAllCount > ProductionCatchAllCeiling)
            violations.Add($"production: catch (Exception) {catchAllCount} exceeds {ProductionCatchAllCeiling}");
        if (literalMutationCount > ProductionLiteralMutationCeiling)
            violations.Add($"production: literal raw mutations {literalMutationCount} exceeds {ProductionLiteralMutationCeiling}");

        Assert.True(violations.Count == 0,
            "Code-style debt ceiling increased:\n" + string.Join("\n", violations));
    }

    private static void CheckCeiling(
        string key,
        string debtKind,
        int actual,
        IReadOnlyDictionary<string, int> ceilings,
        ICollection<string> violations)
    {
        var ceiling = ceilings.GetValueOrDefault(key);
        if (actual > ceiling)
            violations.Add($"{key}: {debtKind} {actual} exceeds {ceiling}");
    }

    private static string ResolveRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Providers", "Stores", "Surreal")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the AZOA repository root.");
    }

    private static bool HasPathSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\.Of\s*\(")]
    private static partial Regex RawQueryRegex();

    [GeneratedRegex("(?ms)SurrealQuery\\s*\\.Of\\(\\s*@?(?:\\\"\\\"\\\"|\\\")\\s*(?:LET\\s+\\$\\w+\\s*=\\s*)?(?<verb>UPDATE ONLY|UPDATE|UPSERT|CREATE|DELETE)\\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RawMutationRegex();

    [GeneratedRegex(@"SurrealQuery\s*\.Of\(\s*sql\s*\)")]
    private static partial Regex DynamicSqlQueryRegex();

    [GeneratedRegex(@"catch\s*\(\s*Exception(?:\s+\w+)?\s*\)(?!\s*when)")]
    private static partial Regex CatchAllRegex();
}
