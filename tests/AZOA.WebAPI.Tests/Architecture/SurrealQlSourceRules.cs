using System.Reflection;
using System.Text.RegularExpressions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Static SurrealQL source rules from the round-4 fail-closed class list; see Architecture/AGENTS.md §surrealql-source-rules.</summary>
internal static partial class SurrealQlSourceRules
{
    /// <summary>The only files allowed to pass non-literal text to <c>SurrealQuery.Of</c> or suppress SRDB0001.</summary>
    public static readonly string[] TemplateHelpers =
    [
        "Core/Surreal/SurrealIdentifierTemplate.cs",
        "Core/Surreal/SurrealGeneratedStatementScope.cs",
        "Core/Surreal/SigningAuthorityFence.cs",
    ];

    private static readonly Lazy<ILookup<string, Type>> ProductionTypes = new(() => typeof(Program).Assembly.GetTypes()
        .Where(type => !type.Name.StartsWith('<'))
        .ToLookup(type => type.Name.Split('`')[0], StringComparer.Ordinal));

    private static readonly Lazy<HashSet<string>> EnumNames = new(() => typeof(Program).Assembly.GetTypes()
        .Where(type => type.IsEnum).Select(type => type.Name).ToHashSet(StringComparer.Ordinal));

    public static IEnumerable<string> SemicolonInOfLiteral(CSharpSource source)
    {
        foreach (var (call, open, close) in OfCalls(source))
        {
            if (open < 0) continue;
            var (start, end) = source.Arguments(open + 1, close).First();
            foreach (var literal in source.Literals.Where(literal => literal.Start >= start && literal.Start < end))
                if (literal.Body.Contains(';'))
                    yield return $"{source.Location(call)} `;` inside a SurrealQuery.Of literal (the library splits statements on it)";
        }
    }

    public static IEnumerable<string> NonLiteralOf(CSharpSource source)
    {
        if (TemplateHelpers.Contains(source.RelativePath)) yield break;
        var constants = ConstantNames(source);
        foreach (var (call, open, close) in OfCalls(source))
        {
            if (open < 0)
            {
                yield return $"{source.Location(call)} SurrealQuery.Of used as a method group (non-literal text)";
                continue;
            }

            var (start, end) = source.Arguments(open + 1, close).First();
            var shape = Shape(source, start, end);
            if (!LiteralShape().IsMatch(shape) && !(IdentifierShape().IsMatch(shape) && constants.Contains(shape)))
                yield return $"{source.Location(call)} SurrealQuery.Of(<non-literal>) outside the template helpers";
        }
    }

    public static IEnumerable<string> Srdb0001Suppression(CSharpSource source)
    {
        if (TemplateHelpers.Contains(source.RelativePath)) yield break;
        foreach (Match match in Srdb0001().Matches(source.Text))
            yield return $"{source.Location(match.Index)} SRDB0001 suppressed outside the template helpers";
    }

    public static IEnumerable<string> AssignOfKey(CSharpSource source)
    {
        foreach (Match match in AssignKey().Matches(source.Code))
            yield return $"{source.Location(match.Index)} Assign(Key(...)) captures a single-record read as an array; use AssignRecord";
    }

    public static IEnumerable<string> ConditionalMutationWithoutWhere(CSharpSource source)
    {
        foreach (Match match in ConditionalMutation().Matches(source.Code))
        {
            var build = source.Code.IndexOf(".Build(", match.Index, StringComparison.Ordinal);
            var statementEnd = source.Code.IndexOf(';', match.Index);
            if (build < 0 || (statementEnd >= 0 && statementEnd < build)) continue;
            if (!WhereCall().IsMatch(source.Code[match.Index..build]))
                yield return $"{source.Location(match.Index)} SurrealWriter.{match.Groups[1].Value} without Where: Build() throws on SurrealForge 1.3.0";
        }
    }

    /// <summary>D-8: tenant payout rows change only through the ledger's atomic commit (plus the create-if-absent float and credit).</summary>
    public static IEnumerable<string> TenantPayoutWriteOutsideLedger(CSharpSource source)
    {
        const string ledger = "Services/Fiat/OffRamp/TenantPayoutLedger.cs";
        foreach (var literal in source.Literals.Where(literal => TenantPayoutRawWrite().IsMatch(literal.Body)))
            yield return $"{source.Location(literal.Start)} raw tenant_payout_* write; go through ITenantPayoutLedger.CommitAsync";
        var mentionsRows = TenantPayoutType().IsMatch(source.Code);
        foreach (Match match in AnyWriter().Matches(source.Code))
        {
            if (source.RelativePath == ledger)
            {
                var member = MemberSignature().Matches(source.Code).LastOrDefault(signature => signature.Index <= match.Index);
                var name = member is null ? "" : MemberName().Match(member.Value).Groups[1].Value;
                if (name is "CommitAsync" or "RecordCreditAsync" or "GetOrCreateFloatAsync") continue;
                yield return $"{source.Location(match.Index)} tenant payout write in {name}; only CommitAsync/RecordCreditAsync/GetOrCreateFloatAsync write";
            }
            else if (mentionsRows && TenantPayoutGeneric().IsMatch(source.Code[match.Index..Math.Min(source.Code.Length, match.Index + 200)]))
                yield return $"{source.Location(match.Index)} typed write of a tenant payout row outside the ledger";
        }
    }

    public static IEnumerable<string> OrdinalEnumCast(CSharpSource source)
    {
        foreach (Match match in OrdinalCast().Matches(source.Code))
            if (EnumNames.Value.Contains(match.Groups[1].Value.Split('.')[^1]))
                yield return $"{source.Location(match.Index)} ordinal enum cast `{match.Value.Trim()}`; map by name";
    }

    public static IEnumerable<string> EndRelativeResultIndex(CSharpSource source)
    {
        var returns = source.Literals.Where(literal => literal.Body.TrimStart().StartsWith("RETURN ", StringComparison.Ordinal)).Select(literal => literal.Start).ToList();
        if (returns.Count == 0) yield break;
        var members = MemberSignature().Matches(source.Code).Select(match => match.Index).Append(source.Code.Length).ToList();
        (int Start, int End) Member(int offset) => (members.LastOrDefault(start => start <= offset), members.First(start => start > offset));
        foreach (Match match in ResultRead().Matches(source.Code))
        {
            var open = match.Index + match.Length - 1;
            var close = source.Close(open);
            var (memberStart, memberEnd) = Member(match.Index);
            if (close > open && EndRelative().IsMatch(source.Code[open..close])
                && returns.Any(offset => offset >= memberStart && offset < memberEnd))
                yield return $"{source.Location(match.Index)} end-relative result index (`Count - k`) in a batch with a top-level RETURN; capture the index before adding RETURN";
        }
    }

    public static IEnumerable<string> BareIdReference(CSharpSource source, IReadOnlyDictionary<string, HashSet<string>>? snippetColumns = null)
    {
        Func<string, HashSet<string>?> Columns = snippetColumns is null ? ProductionResolver(source) : name => snippetColumns.GetValueOrDefault(name);

        foreach (Match match in Initializer().Matches(source.Code))
        {
            if (Columns(match.Groups[1].Value) is not { } columns || (snippetColumns is null && IsNormalizedElsewhere(match.Groups[1].Value, source))) continue;
            var open = match.Index + match.Length - 1;
            var close = source.Close(open);
            if (close < 0) continue;
            foreach (var (start, end) in source.Arguments(open + 1, close))
            {
                var assignment = MemberAssignment().Match(source.Code[start..end]);
                if (!assignment.Success || !columns.Contains(assignment.Groups[1].Value)) continue;
                var valueStart = start + assignment.Index + assignment.Length;
                if (!IsLink(source, valueStart, source.Text[valueStart..end], assignment.Groups[1].Value))
                    yield return $"{source.LocationOfFirstCode(valueStart)} bare id into [References] column {match.Groups[1].Value}.{assignment.Groups[1].Value}";
            }
        }

        foreach (Match match in TypedWriter().Matches(source.Code))
        {
            if (Columns(match.Groups[1].Value) is not { } columns || (snippetColumns is null && IsNormalizedElsewhere(match.Groups[1].Value, source))) continue;
            var statementEnd = source.Code.IndexOf(';', match.Index);
            statementEnd = statementEnd < 0 ? source.Code.Length : statementEnd;
            foreach (Match set in TypedSet().Matches(source.Code[match.Index..statementEnd]))
            {
                if (!columns.Contains(set.Groups[2].Value)) continue;
                var valueStart = match.Index + set.Index + set.Length;
                var open = source.Code.LastIndexOf('(', match.Index + set.Index + set.Length - 1);
                var close = source.Close(open);
                if (close > valueStart && !IsLink(source, valueStart, source.Text[valueStart..close], set.Groups[2].Value))
                    yield return $"{source.LocationOfFirstCode(valueStart)} bare id into [References] column {match.Groups[1].Value}.{set.Groups[2].Value} via typed Set";
            }
        }
    }

    private static string LocationOfFirstCode(this CSharpSource source, int offset)
    {
        while (offset < source.Text.Length && char.IsWhiteSpace(source.Text[offset])) offset++;
        return source.Location(offset);
    }

    /// <summary>POCOs whose store converts bare ids to links in its own ToStorage (round4-sql-sweep "[References] triage" pattern 1); keyed to the store file prefix.</summary>
    public static readonly IReadOnlyDictionary<string, string> NormalizingStores = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["HostedSession"] = "Providers/Stores/Surreal/SurrealHostedSessionStore",
        ["OAuthExchangeCode"] = "Providers/Stores/Surreal/SurrealOAuthExchangeCodeStore",
        ["KycSubmission"] = "Providers/Stores/Surreal/SurrealKycStore",
        ["KycDocument"] = "Providers/Stores/Surreal/SurrealKycStore",
        ["KycControlAudit"] = "Providers/Stores/Surreal/SurrealKycControlStore",
        ["KycProviderProfile"] = "Providers/Stores/Surreal/SurrealKycControlStore",
        ["TenantKycProviderSelection"] = "Providers/Stores/Surreal/SurrealKycControlStore",
        ["KycScreeningResult"] = "Providers/Stores/Surreal/SurrealKycScreeningStore",
        ["KycScreeningMonitor"] = "Providers/Stores/Surreal/SurrealKycScreeningStore",
        ["KycIdentity"] = "Providers/Stores/Surreal/SurrealKycIdentityStore",
        ["PersonOperationClaim"] = "Providers/Stores/Surreal/SurrealPersonOperationClaimStore",
        ["PlatformToken"] = "Providers/Stores/Surreal/SurrealPlatformTokenStore",
        ["Ecosystem"] = "Providers/Stores/Surreal/SurrealEcosystemStore",
        ["EcosystemNode"] = "Providers/Stores/Surreal/SurrealEcosystemStore",
    };

    /// <summary>Reference columns of <paramref name="type"/> that its normalizing store never assigns through a link helper.</summary>
    public static IEnumerable<string> UnnormalizedColumns(string type, IReadOnlyList<CSharpSource> production)
    {
        var store = NormalizingStores[type];
        var text = string.Join("\n", production.Where(source => source.RelativePath.StartsWith(store, StringComparison.Ordinal)).Select(source => source.Text));
        var pocos = ProductionTypes.Value[type].Where(candidate => candidate.GetProperties()
            .Any(property => property.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "ReferencesAttribute"))).ToList();
        if (pocos.Count != 1)
        {
            yield return $"{type} must resolve to exactly one POCO with reference columns (found {pocos.Count})";
            yield break;
        }

        foreach (var column in pocos[0].GetProperties().Where(property => property.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "ReferencesAttribute")))
            if (!Regex.IsMatch(text, $@"\b{column.Name}\s*=(?!=)[^;]*?(?:Link|RecordParam)\w*\s*[.(]", RegexOptions.Singleline))
                yield return $"{store}*.cs never normalizes {type}.{column.Name}";
    }

    private static IEnumerable<Match> LocalInitializers(CSharpSource source, string name)
        => Regex.Matches(source.Text, $@"\b(?:var|string\??)\s+{Regex.Escape(name)}\s*=\s*([^;]+);");

    private static bool IsNormalizedElsewhere(string type, CSharpSource source)
    {
        var alias = Alias().Matches(source.Code).FirstOrDefault(match => match.Groups[1].Value == type);
        var simple = alias is null ? type : alias.Groups[2].Value.Split('.')[^1];
        return NormalizingStores.TryGetValue(simple, out var store) && !source.RelativePath.StartsWith(store, StringComparison.Ordinal);
    }

    private static bool IsLink(CSharpSource source, int offset, string expression, string column, int depth = 0)
    {
        var value = expression.Trim().TrimEnd('!');
        if (depth == 0 && IdentifierShape().IsMatch(value)
            && LocalInitializers(source, value).LastOrDefault(local => local.Index < offset) is { } local)
            return IsLink(source, local.Index, local.Groups[1].Value, column, depth + 1);
        if (value is "null" or "default" or "") return true;
        if (value.Contains("FromLink(", StringComparison.Ordinal) && !value.Contains("ToLink(", StringComparison.Ordinal)) return false;
        if (LinkCall().IsMatch(value)) return true;
        if (TableLiteral().IsMatch(value)) return true;
        var tail = MemberTail().Match(value);
        return tail.Success && (tail.Groups[1].Value == column || tail.Groups[1].Value.EndsWith("Link", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(int Call, int Open, int Close)> OfCalls(CSharpSource source)
    {
        foreach (Match match in OfCall().Matches(source.Code))
        {
            var cursor = match.Index + match.Length;
            while (cursor < source.Code.Length && char.IsWhiteSpace(source.Code[cursor])) cursor++;
            if (cursor < source.Code.Length && source.Code[cursor] == '(')
                yield return (match.Index, cursor, source.Close(cursor));
            else
                yield return (match.Index, -1, -1);
        }
    }

    private static string Shape(CSharpSource source, int start, int end)
    {
        var shape = new System.Text.StringBuilder();
        for (var index = start; index < end; index++)
        {
            if (source.LiteralAt(index) is { } literal)
            {
                shape.Append(literal.Interpolated ? 'I' : 'L');
                index = literal.End - 1;
            }
            else if (!char.IsWhiteSpace(source.Code[index]))
            {
                shape.Append(source.Code[index]);
            }
        }

        return shape.ToString();
    }

    private static HashSet<string> ConstantNames(CSharpSource source)
        => Constant().Matches(source.Code).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>Resolves a simple type name as the compiler would for this file (aliases, usings, enclosing namespaces, nested types); null unless it is exactly one type with reference columns.</summary>
    private static Func<string, HashSet<string>?> ProductionResolver(CSharpSource source)
    {
        var aliases = Alias().Matches(source.Code).ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        var namespaces = Using().Matches(source.Code).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        namespaces.Add("AZOA.WebAPI.Helpers");
        foreach (Match match in Namespace().Matches(source.Code))
            for (var name = match.Groups[1].Value; name.Length > 0; name = name.Contains('.') ? name[..name.LastIndexOf('.')] : "")
                namespaces.Add(name);
        var declared = TypeDeclaration().Matches(source.Code).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return name =>
        {
            var fullAlias = aliases.GetValueOrDefault(name);
            var simple = fullAlias is null ? name : fullAlias.Split('.')[^1];
            var candidates = ProductionTypes.Value[simple].Where(type => fullAlias is not null
                    ? (type.FullName ?? "").Replace('+', '.').EndsWith(fullAlias, StringComparison.Ordinal)
                    : type.DeclaringType is { } outer ? declared.Contains(outer.Name.Split('`')[0]) : namespaces.Contains(type.Namespace ?? ""))
                .ToList();
            if (candidates.Count != 1) return null;
            var columns = candidates[0].GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(property => property.GetCustomAttributes().Any(attribute => attribute.GetType().Name == "ReferencesAttribute"))
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            return columns.Count == 0 ? null : columns;
        };
    }

    [GeneratedRegex(@"\bSurrealQuery\s*\.\s*Of\b(?!\s*<)")]
    private static partial Regex OfCall();

    [GeneratedRegex(@"^(?:[\w.!()&|=<>]+\?)?L(\+L)*(?::L(\+L)*)?$")]
    private static partial Regex LiteralShape();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex IdentifierShape();

    [GeneratedRegex(@"\bconst\s+string\s+(\w+)\s*=")]
    private static partial Regex Constant();

    [GeneratedRegex(@"#pragma\s+warning\s+disable[^\n]*SRDB0001|SuppressMessage\s*\([^)]*SRDB0001")]
    private static partial Regex Srdb0001();

    [GeneratedRegex(@"\bAssign\s*\(\s*(?:SurrealQuery\s*<[^()]*?>\s*\.\s*)?Key\s*\(")]
    private static partial Regex AssignKey();

    [GeneratedRegex(@"\(\s*([A-Z][\w.]*)\??\s*\)\s*\(\s*(?:int|long|short|byte|uint|ulong)\s*\)")]
    private static partial Regex OrdinalCast();

    [GeneratedRegex(@"\b(?:UPDATE|UPSERT|CREATE|DELETE|INSERT|RELATE)\b[^;]*\btenant_payout_(?:float|credit|debit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TenantPayoutRawWrite();

    [GeneratedRegex(@"\bTenantPayout(?:Float|Credit|Debit)\b")]
    private static partial Regex TenantPayoutType();

    [GeneratedRegex(@"^\s*SurrealWriter\s*\.\s*(?:Create|Upsert|UpdateOnly|DeleteOnly)\s*(?:<\s*TenantPayout(?:Float|Credit|Debit)\s*>|\(\s*(?:new\s+TenantPayout|\w*(?:[Ff]loat|[Cc]redit|[Dd]ebit)\w*\s*\)))")]
    private static partial Regex TenantPayoutGeneric();

    [GeneratedRegex(@"\bSurrealWriter\s*\.\s*(?:Create|Upsert|UpdateOnly|DeleteOnly)\b")]
    private static partial Regex AnyWriter();

    [GeneratedRegex(@"(\w+)\s*(?:<[^>]*>)?\s*\($")]
    private static partial Regex MemberName();

    [GeneratedRegex(@"\bSurrealWriter\s*\.\s*(UpdateOnly|DeleteOnly)\s*<")]
    private static partial Regex ConditionalMutation();

    [GeneratedRegex(@"\.\s*Where\s*\(")]
    private static partial Regex WhereCall();

    [GeneratedRegex(@"\bGetValues?\s*(?:<[^()]*?>)?\s*\(")]
    private static partial Regex ResultRead();

    [GeneratedRegex(@"^[ \t]*(?:(?:public|private|internal|protected|static|async|override|virtual|sealed|partial)\s+)+[\w<>\[\]?,. ()]+\s+\w+\s*(?:<[^>]*>)?\s*\(", RegexOptions.Multiline)]
    private static partial Regex MemberSignature();

    [GeneratedRegex(@"\.\s*Count\s*-\s*\d+")]
    private static partial Regex EndRelative();

    [GeneratedRegex(@"\busing\s+(\w+)\s*=\s*([\w.]+)\s*;")]
    private static partial Regex Alias();

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+([\w.]+)\s*;", RegexOptions.Multiline)]
    private static partial Regex Using();

    [GeneratedRegex(@"\bnamespace\s+([\w.]+)")]
    private static partial Regex Namespace();

    [GeneratedRegex(@"\b(?:class|record|struct)\s+(\w+)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"\bnew\s+(?:[\w.]+\.)?(\w+)\s*(?:\(\s*\))?\s*\{")]
    private static partial Regex Initializer();

    [GeneratedRegex(@"^\s*(\w+)\s*=(?!=)")]
    private static partial Regex MemberAssignment();

    [GeneratedRegex(@"\bSurrealWriter\s*\.\s*(?:UpdateOnly|Update|Upsert|Create)\s*<\s*(?:[\w.]+\.)?(\w+)\s*>")]
    private static partial Regex TypedWriter();

    [GeneratedRegex(@"\.\s*Set\s*\(\s*(\w+)\s*=>\s*\1\s*\.\s*(\w+)\s*,")]
    private static partial Regex TypedSet();

    [GeneratedRegex(@"\b\w*(?:Link|RecordParam)\w*\s*[.(]|\bSurrealLink\b|\bSurrealRecordParam\b")]
    private static partial Regex LinkCall();

    [GeneratedRegex(@"^\$?@?""[a-z_][a-z0-9_]*:")]
    private static partial Regex TableLiteral();

    [GeneratedRegex(@"^(?:[\w]+\??\.)*([\w]+)$")]
    private static partial Regex MemberTail();
}
