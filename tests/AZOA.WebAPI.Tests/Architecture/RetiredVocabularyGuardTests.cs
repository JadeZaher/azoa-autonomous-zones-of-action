using System.Text.RegularExpressions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>
/// Retired-vocabulary ratchet over shipped source. See <c>Architecture/AGENTS.md</c>
/// §Retired vocabulary ratchet.
/// </summary>
public sealed partial class RetiredVocabularyGuardTests
{
    private static readonly string[] ScannedRoots =
    [
        "Controllers", "Managers", "Services", "Core", "Interfaces", "Models", "Providers",
        "Persistence", "Validators", "Mcp", "Helpers", "Extensions", "Middleware",
        "Observability", "Settings", "Properties", "deploy",
        "Program.cs", "GlobalUsings.cs", "Dockerfile", "railway.json",
        "sdk/azoa-wallet/src", "sdk/azoa-wallet/package.json", "sdk/azoa-wallet/README.md",
        "frontend/src", "frontend/public",
    ];

    /// <summary>Checksum-ledgered history: shipped bytes may never change.</summary>
    private static readonly string[] SkippedRelativeDirectories =
    [
        "Persistence/SurrealDb/CompatibilityBaselines",
        "Persistence/SurrealDb/Migrations",
    ];

    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ts", ".tsx", ".js", ".mjs", ".json", ".surql", ".yml", ".yaml", ".toml", ".py", ".sh",
    };

    private static readonly HashSet<string> SkippedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".next", "dist" };

    /// <summary>Lowercased whole words; identifiers are split first, so "request" never matches "quest".</summary>
    private static readonly HashSet<string> RetiredWords = new(StringComparer.Ordinal)
    {
        "quest", "quests", "avatar", "avatars",
        "odk", "odks", "starodk", "starodks", "istar", "istarodk",
        "karma", "game", "games", "gaming", "gamified", "gamification", "player", "players",
    };

    /// <summary>Word parts that, after "Star", spelled a pre-10f4c16 identifier (StarGenerate, _starManager).</summary>
    private static readonly HashSet<string> StarCompanions = new(StringComparer.Ordinal)
    {
        "Generate", "Deploy", "Manager", "Store", "Service", "Controller", "Dapp", "Odk", "ODK", "Id", "Ids",
    };

    private static readonly Dictionary<string, string> ExemptFiles = new(StringComparer.Ordinal)
    {
        ["frontend/src/components/ui/avatar.tsx"] = "the base-ui Avatar primitive's own wrapper",
    };

    private const string AvatarPrimitiveImport = "components/ui/avatar";

    [Fact]
    public void Shipped_source_carries_no_retired_vocabulary()
    {
        var root = ResolveRepoRoot();
        var emptyRoots = ScannedRoots.Where(relative => !FilesUnder(root, relative).Any()).ToList();
        emptyRoots.Should().BeEmpty("a renamed or missing root silently drops coverage instead of failing");

        var offenders = new List<string>();
        foreach (var path in ScannedRoots.SelectMany(relative => FilesUnder(root, relative))
                     .Concat(Directory.EnumerateFiles(root, "appsettings*.json", SearchOption.TopDirectoryOnly)))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (ExemptFiles.ContainsKey(relative))
                continue;

            offenders.AddRange(RetiredWordsIn(relative).Select(word => $"{relative} (path): {word}"));

            var lines = File.ReadAllLines(path);
            var usesAvatarPrimitive = lines.Any(line => line.Contains(AvatarPrimitiveImport, StringComparison.Ordinal));
            for (var index = 0; index < lines.Length; index++)
            {
                var words = RetiredWordsIn(lines[index]);
                if (usesAvatarPrimitive)
                    words = words.Where(word => !IsAvatarPrimitiveToken(word, lines[index]));
                offenders.AddRange(words.Select(word => $"{relative}:{index + 1}: {word}"));
            }
        }

        offenders.Should().BeEmpty(
            "D28 retired Avatar/Quest, 10f4c16 retired STAR/ODK, and game vocabulary left the product — "
            + "say User, Action, or ActionGraphBundle; a genuine third-party name goes in ExemptFiles with its reason");
    }

    [Theory]
    [InlineData("var run = await StartQuestRunAsync(id);", true)]
    [InlineData("public interface IAvatarStore", true)]
    [InlineData("class StarGenerateNodeHandler", true)]
    [InlineData("private readonly IStarManager _starManager;", true)]
    [InlineData("var starId = model.Id;", true)]
    [InlineData("\"path\":\"/api/star/{id}\"", true)]
    [InlineData("List all ODKs", true)]
    [InlineData("LoadAllSTARODKsAsync", true)]
    [InlineData("\"name\":\"XSSODK\"", true)]
    [InlineData("{{e2e2ODK.id}}", true)]
    [InlineData("the STAR dapp generator", true)]
    [InlineData("Player journeys with real stakes", true)]
    [InlineData("var request = new HttpRequestMessage();", false)]
    [InlineData("questionnaire and RequestQuestion", false)]
    [InlineData("ACTION_START_WORKFLOW / FIAT_CHECKOUT_START / RESTART", false)]
    [InlineData("<Star className=\"h-3.5 w-3.5\" />", false)]
    [InlineData("StarTable lowercases to startable", false)]
    [InlineData("Deleted in Mission B", false)]
    [InlineData("Gamepad2 multiplayer APIs IDs", false)]
    public void Matcher_flags_retired_shapes_and_spares_lookalikes(string line, bool retired)
    {
        RetiredWordsIn(line).Any().Should().Be(retired, $"'{line}'");
    }

    private static bool IsAvatarPrimitiveToken(string word, string line) =>
        word == "Avatar" || (word == "avatar" && line.Contains(AvatarPrimitiveImport, StringComparison.Ordinal));

    private static IEnumerable<string> RetiredWordsIn(string line)
    {
        if (LegacyStarRouteRegex().IsMatch(line))
            yield return "api/star";

        foreach (Match run in LetterRunRegex().Matches(line))
        {
            var parts = WordPartRegex().Matches(run.Value).Select(part => part.Value).ToList();
            for (var index = 0; index < parts.Count; index++)
            {
                var next = index + 1 < parts.Count ? parts[index + 1] : null;
                if (IsRetired(parts[index], next))
                    yield return parts[index];
            }
        }
    }

    private static bool IsRetired(string part, string? next)
    {
        if (RetiredWords.Contains(part.ToLowerInvariant()) || part is "STAR" or "STARs")
            return true;
        if (part.All(char.IsUpper) && part.EndsWith("ODK", StringComparison.Ordinal))
            return true; // glued acronyms: XSSODK, STARODK
        return part is "Star" or "star" && next is not null && StarCompanions.Contains(next);
    }

    private static IEnumerable<string> FilesUnder(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
            return [path];
        if (!Directory.Exists(path))
            return [];

        var skipped = SkippedRelativeDirectories
            .Select(directory => Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Walk(path, skipped).Where(file => ScannedExtensions.Contains(Path.GetExtension(file)));
    }

    private static IEnumerable<string> Walk(string directory, HashSet<string> skipped)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
            yield return file;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectoryNames.Contains(Path.GetFileName(child)) || skipped.Contains(child))
                continue;
            foreach (var file in Walk(child, skipped))
                yield return file;
        }
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

    [GeneratedRegex("[A-Za-z]+")]
    private static partial Regex LetterRunRegex();

    /// <summary>Acronym plural, then acronym, then camel word: "ODKs", "ISTAR", "Manager".</summary>
    [GeneratedRegex("[A-Z]{2,}s(?![a-z])|[A-Z]+(?![a-z])|[A-Z]?[a-z]+")]
    private static partial Regex WordPartRegex();

    [GeneratedRegex(@"\bapi/star(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyStarRouteRegex();
}
