using System.Text.RegularExpressions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Mechanical gate for round3-transaction-scope-review.md: batches commit, egress writes serialize on the operation row, in-batch reads name their lock; see Architecture/AGENTS.md §transaction-scope-gate.</summary>
public sealed partial class TransactionScopeGateTests
{
    private static readonly Lazy<IReadOnlyList<CSharpSource>> Production = new(CSharpSource.Production);

    /// <summary>Store family → the only member allowed to commit an egress (AuthorizedAt / ExternalExposedAt) write; it also CAS-writes the operation row.</summary>
    private static readonly Dictionary<string, string> EgressCommitters = new(StringComparer.Ordinal)
    {
        ["SurrealSwapOperationStore"] = "CommitStageAsync",
        ["SurrealWttOperationStore"] = "CommitAsync",
    };

    /// <summary>Batches that need no THROW guard: no pre-read decision, the atomicity is the point.</summary>
    private static readonly Dictionary<string, string> GuardlessBatches = new(StringComparer.Ordinal)
    {
        ["Providers/Stores/Surreal/SurrealActionRunStore.cs"] = "fork run CREATE + fork edge RELATE; a duplicate id fails the CREATE itself",
        ["Helpers/KycEvidenceFence.cs"] = "wrapper: prepends the evidence-guard UPSERT; the wrapped caller mutation carries its own THROW guards",
    };

    /// <summary>BEGIN openers committed through a delegate the call-graph scan cannot follow.</summary>
    private static readonly Dictionary<string, string> DelegateCommitted = new(StringComparer.Ordinal)
    {
        ["SurrealHoldCommandStore.SourceTransition"] = "opens a TerminalPlan; TerminalAsync passes it to CommitTerminalAsync, which appends COMMIT (Contention.cs)",
    };

    /// <summary>Files whose every in-batch read carries a `// lock:` annotation (ratchet; add a file once it is annotated).</summary>
    private static readonly string[] LockAnnotated =
    [
        "Helpers/KycEvidenceFence.cs",
        "Providers/Stores/Surreal/SurrealAdminBootstrapStateStore.cs",
        "Providers/Stores/Surreal/SurrealNodeFeeScheduleStore.cs",
        "Providers/Stores/Surreal/SurrealNodeGovernanceStore.cs",
        "Providers/Stores/Surreal/SurrealNodeTreasuryStore.cs",
        "Services/Auth/WalletAuthIdentityMigrationService.cs",
        "Providers/Stores/Surreal/SurrealGovernanceStore.cs",
        "Providers/Stores/Surreal/SurrealGovernanceStore.Finalization.cs",
        "Providers/Stores/Surreal/SurrealGovernanceStore.Voting.cs",
        "Providers/Stores/Surreal/SurrealActionWebhookDeliveryStore.Management.cs",
        "Providers/Stores/Surreal/SurrealConsentWebhookDeliveryStore.Management.cs",
        "Providers/Stores/Surreal/HoldReleaseExchangeFence.cs",
        "Providers/Stores/Surreal/HoldReleaseWorkflowFence.cs",
    ];

    public static TheoryData<string, string, bool> Snippets => new()
    {
        { "commit", """
            public async Task A() { var s = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"), guard, SurrealQuery.Of("IF x { THROW 'X' }") }; await executor.ExecuteAsync(SurrealQuery.Combine(s.ToArray())); }
            """, true },
        { "commit", """
            public async Task A() { var s = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"), SurrealQuery.Of("IF x { THROW 'X' }") }; await CommitAsync(s); }
            private async Task CommitAsync(List<SurrealQuery> s) { s.Add(SurrealQuery.Of("COMMIT")); await executor.ExecuteAsync(SurrealQuery.Combine(s.ToArray())); }
            """, false },
        { "commit", """
            private static List<SurrealQuery> Open() => [SurrealQuery.Of("BEGIN"), SurrealQuery.Of("IF x { THROW 'X' }")];
            public async Task A() { var s = Open(); s.Add(SurrealQuery.Of("COMMIT")); await executor.ExecuteAsync(SurrealQuery.Combine(s.ToArray())); }
            """, false },
        { "guard", """
            public async Task A() { await executor.ExecuteAsync(SurrealQuery.Combine(SurrealQuery.Of("BEGIN"), read, write, SurrealQuery.Of("COMMIT"))); }
            """, true },
        { "egress", """
            public async Task<Stage?> Authorize() { var stage = SurrealWriter.UpdateOnly<Stage>(id).Where(row => row.Version == v).Set(row => row.AuthorizedAt, now); return await SaveAsync(stage.Build()); }
            """, true },
        { "egress", """
            public async Task<Stage?> Authorize() { var stage = SurrealWriter.UpdateOnly<Stage>(id).Where(row => row.Version == v).Set(row => row.AuthorizedAt, now); return await CommitStageAsync(guards, operation, stage.Build(), ct); }
            """, false },
        { "lock", "var s = SurrealQuery.Combine(SurrealQuery.Of(\"BEGIN\"),\n    SurrealQuery.Of(\"LET $_v = SELECT VALUE version FROM type::record($_t, $_id)\"),\n    SurrealQuery.Of(\"COMMIT\"));", true },
        { "lock", "var s = SurrealQuery.Combine(SurrealQuery.Of(\"BEGIN\"),\n    // lock: the row read here is upserted below in this batch.\n    SurrealQuery.Of(\"LET $_v = SELECT VALUE version FROM type::record($_t, $_id)\"),\n    SurrealQuery.Of(\"COMMIT\"));", false },
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void Each_rule_is_red_on_its_pre_fix_pattern_and_green_on_the_fix(string rule, string snippet, bool violates)
    {
        var source = CSharpSource.Parse("Providers/Stores/Surreal/SurrealSwapOperationStore.Snippet.cs", "class S {\n" + snippet + "\n}");
        var family = new[] { source };
        var found = rule switch
        {
            "commit" => UncommittedBatches(family),
            "guard" => UnguardedBatches(family),
            "egress" => EgressOutsideCommitter(family, rule == "egress" ? new() { ["SurrealSwapOperationStore"] = "CommitStageAsync" } : EgressCommitters),
            "lock" => UnannotatedReads(source),
            _ => throw new ArgumentOutOfRangeException(nameof(rule)),
        };
        found.Any().Should().Be(violates, snippet);
    }

    [Fact]
    public void Every_BEGIN_batch_reaches_a_COMMIT()
        => Families().SelectMany(UncommittedBatches).Should().BeEmpty("an unterminated BEGIN leaves the statements outside any transaction");

    [Fact]
    public void Every_batch_that_decides_carries_a_THROW_guard()
        => Families().SelectMany(UnguardedBatches).Should().BeEmpty("a failed predicate must THROW so the whole batch rolls back (house rule 1)");

    [Fact]
    public void Egress_writes_commit_only_through_the_operation_row_CAS()
        => EgressOutsideCommitter(Production.Value, EgressCommitters).Should().BeEmpty(
            "every egress write and every exit from Cancelled must update the operation row the allowance claim no-op-locks (round3-transaction-scope-review.md)");

    [Fact]
    public void Ratcheted_files_annotate_every_in_batch_read()
    {
        var sources = Production.Value.ToDictionary(source => source.RelativePath);
        LockAnnotated.Where(path => !sources.ContainsKey(path)).Should().BeEmpty("a renamed file must keep its ratchet entry");
        LockAnnotated.Where(sources.ContainsKey).SelectMany(path => UnannotatedReads(sources[path]))
            .Should().BeEmpty("each in-batch read names its serialization row (`// lock: <row>`) or it is a write-skew hole");
    }

    private static IEnumerable<IReadOnlyList<CSharpSource>> Families()
        => Production.Value.Where(source => source.Text.Contains("Of(\"BEGIN\")", StringComparison.Ordinal) || source.Text.Contains("Of(\"COMMIT\")", StringComparison.Ordinal))
            .GroupBy(source => FamilyName(source))
            .Select(group => Production.Value.Where(source => FamilyName(source) == group.Key).ToList());

    private static string FamilyName(CSharpSource source) => Path.GetFileName(source.RelativePath).Split('.')[0];

    private static IEnumerable<string> UncommittedBatches(IReadOnlyList<CSharpSource> family)
    {
        var members = family.SelectMany(source => Members(source).Select(member => (source, member))).ToList();
        var committing = members.Where(pair => pair.member.Body.Contains("Of(\"COMMIT\")", StringComparison.Ordinal))
            .Select(pair => pair.member.Name).ToHashSet(StringComparer.Ordinal);
        // Fixpoint: a member reaches COMMIT if it adds it or calls a member that does.
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (_, candidate) in members)
                if (!committing.Contains(candidate.Name) && committing.Any(name => Regex.IsMatch(candidate.Body, $@"\b{name}\s*\(")))
                    grew |= committing.Add(candidate.Name);
        }

        foreach (var (source, member) in members.Where(pair => pair.member.Body.Contains("Of(\"BEGIN\")", StringComparison.Ordinal)))
        {
            var callsCommitter = committing.Any(name => Regex.IsMatch(member.Body, $@"\b{name}\s*\("));
            var calledByCommitter = members.Any(pair => committing.Contains(pair.member.Name)
                && Regex.IsMatch(pair.member.Body, $@"\b{member.Name}\s*\("));
            if (!committing.Contains(member.Name) && !callsCommitter && !calledByCommitter
                && !DelegateCommitted.ContainsKey($"{FamilyName(source)}.{member.Name}"))
                yield return $"{source.Location(member.Start)} {member.Name}: BEGIN without a COMMIT in it, its callee or its caller";
        }
    }

    private static IEnumerable<string> UnguardedBatches(IReadOnlyList<CSharpSource> family)
    {
        foreach (var source in family.Where(source => source.Text.Contains("Of(\"BEGIN\")", StringComparison.Ordinal)))
        {
            if (GuardlessBatches.ContainsKey(source.RelativePath)) continue;
            if (!family.Any(candidate => Guard().IsMatch(candidate.Text)))
                yield return $"{source.RelativePath}: BEGIN batch family without any THROW guard";
        }
    }

    private static IEnumerable<string> EgressOutsideCommitter(IEnumerable<CSharpSource> sources, IReadOnlyDictionary<string, string> committers)
    {
        foreach (var source in sources)
            foreach (Match match in EgressSet().Matches(source.Code))
            {
                var member = Members(source).LastOrDefault(candidate => candidate.Start <= match.Index && match.Index < candidate.End);
                var committer = committers.GetValueOrDefault(FamilyName(source));
                if (committer is null || member is null || !Regex.IsMatch(member.Body, $@"\b{committer}\s*\("))
                    yield return $"{source.Location(match.Index)} egress {match.Groups[1].Value} written outside {committer ?? "a registered egress committer"}";
            }
    }

    private static IEnumerable<string> UnannotatedReads(CSharpSource source)
    {
        if (!source.Text.Contains("Of(\"BEGIN\")", StringComparison.Ordinal) && !source.RelativePath.EndsWith("Fence.cs", StringComparison.Ordinal)) yield break;
        var reads = source.Literals.Where(literal => Select().IsMatch(literal.Body)).Select(literal => literal.Start)
            .Concat(InBatchRead().Matches(source.Code).Select(match => match.Index));
        var members = Members(source);
        foreach (var offset in reads.Distinct().Order())
        {
            // A `// lock:` comment covers every later read in the same member (one serialization argument per batch).
            var start = members.LastOrDefault(member => member.Start <= offset)?.Start ?? 0;
            if (!source.Text[start..offset].Contains("// lock:", StringComparison.Ordinal))
                yield return $"{source.Location(offset)} in-batch read without a `// lock:` annotation";
        }
    }

    private sealed record Member(string Name, int Start, int End, string Body);

    private static List<Member> Members(CSharpSource source)
    {
        var starts = MemberSignature().Matches(source.Code).Select(match => (match.Groups[1].Value, match.Index)).ToList();
        return starts.Select((start, index) =>
        {
            var end = index + 1 < starts.Count ? starts[index + 1].Index : source.Code.Length;
            return new Member(start.Value, start.Index, end, source.Text[start.Index..end]);
        }).ToList();
    }

    [GeneratedRegex(@"^[ \t]*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|partial)\s+)+[\w<>\[\]?,. ()]+?\s+(\w+)\s*(?:<[^>]*>)?\s*\(", RegexOptions.Multiline)]
    private static partial Regex MemberSignature();

    [GeneratedRegex(@"\bTHROW\b|\bThrow\s*\(|\bRequireResult\s*\(|Fence\s*\.\s*\w+\s*\(")]
    private static partial Regex Guard();

    [GeneratedRegex(@"\.\s*Set\s*\(\s*\w+\s*=>\s*\w+\s*\.\s*(AuthorizedAt|ExternalExposedAt)\b")]
    private static partial Regex EgressSet();

    [GeneratedRegex(@"\bSELECT\b")]
    private static partial Regex Select();

    [GeneratedRegex(@"\bAssignRecord\s*\(|\bAssign\s*\(\s*SurrealQuery\s*<")]
    private static partial Regex InBatchRead();
}
