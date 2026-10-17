using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Round-4 SurrealQL source rules over production code, each pinned red/green by a pre-fix/post-fix snippet; see Architecture/AGENTS.md §surrealql-source-rules.</summary>
public sealed class SurrealQlSourceRulesTests
{
    private static readonly Lazy<IReadOnlyList<CSharpSource>> Production = new(CSharpSource.Production);

    private static readonly Dictionary<string, HashSet<string>> SnippetColumns = new()
    {
        ["TenantPriceSchedule"] = ["TenantId", "RegisteredByUserId"],
        ["NftOwnership"] = ["UserId"],
    };

    public static TheoryData<string, string, bool> Snippets => new()
    {
        // rule, snippet, expected violation (pre-fix = true, post-fix = false)
        { "semicolon", """var q = SurrealQuery.Of("UPDATE a SET b = 1; UPDATE c SET d = 2");""", true },
        { "semicolon", """var q = SurrealQuery.Of("UPDATE a SET b = $_b").WithParam("_b", ";");""", false },
        { "semicolon", "var q = SurrealQuery\n    .Of(\"\"\"\n    LET $x = 1; RETURN $x\n    \"\"\");", true },
        { "non-literal", """var q = SurrealQuery.Of(sql);""", true },
        { "non-literal", """var q = SurrealQuery.Of($"SELECT * FROM {table}");""", true },
        { "non-literal", """var all = text.Split('|').Select(SurrealQuery.Of);""", true },
        { "non-literal", """var q = SurrealQuery.Of("SELECT " + "* FROM a");""", false },
        { "non-literal", "private const string Erase = \"DELETE a\";\nvar q = SurrealQuery.Of(Erase);", false },
        { "srdb0001", "#pragma warning disable SRDB0001\nvar q = 1;", true },
        { "srdb0001", "var q = 1;", false },
        { "assign-key", """var s = SurrealGeneratedStatementScope.Assign(SurrealQuery<Row>.Key(id), "x", "_x");""", true },
        { "assign-key", """var s = SurrealGeneratedStatementScope.AssignRecord(SurrealQuery<Row>.Key(id), "x", "_x");""", false },
        { "ordinal", "var state = (Row.ActionNodeState)(int)execution.State;", true },
        { "ordinal", "var state = Enum.Parse<Row.ActionNodeState>(execution.State.ToString());", false },
        { "end-relative", "statements.Add(SurrealQuery.Of(\"RETURN $_user\"));\nvar row = response.GetValues<User>(statements.Count - 2).Single();", true },
        { "end-relative", "var resultIndex = statements.Count;\nstatements.Add(SurrealQuery.Of(\"RETURN $_user\"));\nvar row = response.GetValues<User>(resultIndex).Single();", false },
        { "bare-id", """var row = new TenantPriceSchedule { TenantId = tenantId.ToString("N"), Name = name };""", true },
        { "bare-id", """var row = new TenantPriceSchedule { TenantId = SurrealLink.ToLink("user", tenantId.ToString("N")), Name = name };""", false },
        { "bare-id", """var row = new TenantPriceSchedule { TenantId = existing.TenantId, RegisteredByUserId = actorLink };""", false },
        { "bare-id", """var row = new TenantPriceSchedule { RegisteredByUserId = SurrealLink.FromLink(link) };""", true },
        { "bare-id", """var write = SurrealWriter.UpdateOnly<NftOwnership>(id).Set(row => row.UserId, authority.HolderUserId).Build();""", true },
        { "bare-id", """var write = SurrealWriter.UpdateOnly<NftOwnership>(id).Set(row => row.UserId, SurrealLink.ToLink("user", authority.HolderUserId)!).Build();""", false },
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void Each_rule_is_red_on_its_pre_fix_pattern_and_green_on_the_fix(string rule, string snippet, bool violates)
    {
        var source = CSharpSource.Parse("Providers/Stores/Surreal/Snippet.cs", snippet);
        Run(rule, source).Any().Should().Be(violates, snippet);
    }

    [Fact]
    public void No_semicolon_inside_SurrealQuery_Of_literals()
        => AssertNone("semicolon", "SurrealForge splits Of(...) on `;` and throws (round4-sql-sweep F-H13)");

    [Fact]
    public void SurrealQuery_Of_takes_only_literal_text_outside_the_template_helpers()
        => AssertNone("non-literal", "a `var sql` indirection slips past SRDB0001 (round4-sql-sweep SQL-13)");

    [Fact]
    public void SRDB0001_is_suppressed_only_in_the_template_helpers()
        => AssertNone("srdb0001", "identifier templating is localized to the reviewed helpers (round4-plan A-1)");

    [Fact]
    public void Project_files_never_silence_SRDB0001()
    {
        var root = CSharpSource.RepositoryRoot();
        var offenders = Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, ".editorconfig", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(root, "Directory.Build.*", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(root, "GlobalSuppressions.cs", SearchOption.TopDirectoryOnly))
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path),
                @"<NoWarn>[^<]*SRDB0001|dotnet_diagnostic\.SRDB0001\.severity\s*=\s*(none|silent|suggestion)|SuppressMessage\s*\([^)]*SRDB0001"))
            .Select(path => Path.GetFileName(path));
        offenders.Should().BeEmpty("a project-wide NoWarn/severity override would disable the analyzer everywhere");
    }

    [Fact]
    public void Single_record_reads_are_never_captured_with_Assign()
        => AssertNone("assign-key", "Assign wraps the read as an array; guards comparing it to NONE never fire");

    [Fact]
    public void Persisted_enums_are_never_cast_by_ordinal()
        => AssertNone("ordinal", "a member reorder silently remaps states, including inside CAS guards (round4-sql-sweep SQL-10)");

    [Fact]
    public void Results_are_never_read_end_relative_after_a_top_level_RETURN()
        => AssertNone("end-relative", "a top-level RETURN drops COMMIT from the result array (round4-sql-sweep SQL-01/02)");

    [Fact]
    public void Reference_columns_are_never_assigned_a_bare_id()
        => AssertNone("bare-id", "record<T> columns reject bare text; the write fails after its side effects (round4-sql-sweep D2/SQL-03..08)");

    /// <summary>Ratchet of reviewed residue: (rule, file) → exact count + owning fix. A new site fails; a fixed site must be removed here.</summary>
    private static readonly Dictionary<(string Rule, string File), (int Count, string Owner)> KnownViolations = new()
    {
    };

    private static void AssertNone(string rule, string because)
    {
        var found = Production.Value.SelectMany(source => Run(rule, source)).ToList();
        var problems = new List<string>();
        foreach (var group in found.GroupBy(violation => violation[..violation.IndexOf(':')]))
        {
            var known = KnownViolations.GetValueOrDefault((rule, group.Key));
            if (group.Count() > known.Count)
                problems.AddRange(group.Select(violation => known.Count == 0 ? violation : $"{violation} (known {known.Count}, owner {known.Owner})"));
        }

        foreach (var ((knownRule, file), (count, owner)) in KnownViolations.Where(entry => entry.Key.Rule == rule))
        {
            var actual = found.Count(violation => violation.StartsWith(file + ":", StringComparison.Ordinal));
            if (actual < count)
                problems.Add($"{file}: {knownRule} residue fixed ({actual} < {count}, {owner}); lower or remove its KnownViolations entry");
        }

        Assert.True(problems.Count == 0, because + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Stores_trusted_to_normalize_reference_columns_actually_link_every_one()
        => SurrealQlSourceRules.NormalizingStores.Keys
            .SelectMany(type => SurrealQlSourceRules.UnnormalizedColumns(type, Production.Value))
            .Should().BeEmpty("the bare-id rule skips callers of these POCOs only because their store links each column in ToStorage");

    private static IEnumerable<string> Run(string rule, CSharpSource source) => rule switch
    {
        "semicolon" => SurrealQlSourceRules.SemicolonInOfLiteral(source),
        "non-literal" => SurrealQlSourceRules.NonLiteralOf(source),
        "srdb0001" => SurrealQlSourceRules.Srdb0001Suppression(source),
        "assign-key" => SurrealQlSourceRules.AssignOfKey(source),
        "ordinal" => SurrealQlSourceRules.OrdinalEnumCast(source),
        "end-relative" => SurrealQlSourceRules.EndRelativeResultIndex(source),
        "bare-id" => SurrealQlSourceRules.BareIdReference(source, source.RelativePath.EndsWith("Snippet.cs", StringComparison.Ordinal) ? SnippetColumns : null),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, null),
    };
}
