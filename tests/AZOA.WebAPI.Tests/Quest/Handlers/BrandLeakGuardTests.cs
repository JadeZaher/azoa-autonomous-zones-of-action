using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace AZOA.WebAPI.Tests.Quest.Handlers;

/// <summary>
/// T13 — brand-leak guard (economic-primitive-nodes).
///
/// <para>The Holon-transformation nodes are tenant-agnostic <b>mechanism</b>:
/// no tenant economic brand or instrument vocabulary may leak into the
/// executable logic of the handler, predicate, or config source. This test
/// reads those source files from the repository tree at test time and asserts
/// that none of their <b>active code</b> contains (case-insensitively) a
/// tenant-economic brand/instrument token.</para>
///
/// <para><b>Comments are stripped before scanning.</b> The guard targets a
/// mechanism leak — a tenant brand/instrument bleeding into identifiers,
/// strings, or branch logic — not documentation. A doc comment may legitimately
/// name the canonical example consumer (e.g. "ArdaNova reads the run and settles
/// tenant-side") to explain intent; that is documentation, not coupling. The
/// economic-instrument tokens (<c>equity</c>, <c>vesting</c>,
/// <c>project-token</c>) must not appear in code at all (and in fact appear
/// nowhere in the tree, comments included).</para>
///
/// <para>The forbidden tokens are the SPECIFIC brand/instrument strings —
/// <c>ArdaNova</c>, <c>project-token</c>/<c>projectToken</c>, <c>equity</c>,
/// <c>vesting</c>. A bare <c>"project"</c> substring is deliberately NOT scanned:
/// it legitimately appears in MSBuild/namespace/"project root" contexts and
/// would false-positive.</para>
/// </summary>
public class BrandLeakGuardTests
{
    // Specific brand/instrument tokens only — NOT bare "project" (too broad:
    // it appears in MSBuild/namespace/"project root" contexts).
    private static readonly string[] ForbiddenTokens =
    {
        "ArdaNova",
        "project-token",
        "projectToken",
        "equity",
        "vesting",
    };

    // Source areas this track owns / must keep brand-free.
    private static readonly string[] ScannedRelativeDirs =
    {
        Path.Combine("Services", "Quest", "Handlers"),
        Path.Combine("Services", "Quest", "Predicates"),
    };

    // The config POCOs added by this track live in a single file.
    private static readonly string ScannedConfigFile =
        Path.Combine("Models", "Quest", "NodeConfigs.cs");

    [Fact]
    public void HandlerPredicateAndConfigCode_ContainsNoTenantEconomicBrandStrings()
    {
        var repoRoot = ResolveRepoRoot();

        var files = ScannedRelativeDirs
            .Select(rel => Path.Combine(repoRoot, rel))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Append(Path.Combine(repoRoot, ScannedConfigFile))
            .Distinct()
            .ToList();

        // Robustness: if path resolution failed, fail LOUDLY rather than passing
        // on an empty scan set.
        files.Should().NotBeEmpty(
            "the brand-leak scan must locate the handler/predicate/config source — " +
            $"resolved repo root: '{repoRoot}'. If empty, ResolveRepoRoot found the wrong directory.");
        File.Exists(Path.Combine(repoRoot, ScannedConfigFile)).Should().BeTrue(
            $"the config POCO file must exist at '{ScannedConfigFile}' under repo root '{repoRoot}'");

        var violations = files
            .SelectMany(file =>
            {
                var code = StripComments(File.ReadAllText(file));
                return ForbiddenTokens
                    .Where(token => code.Contains(token, StringComparison.OrdinalIgnoreCase))
                    .Select(token => $"{file}: '{token}'");
            })
            .ToList();

        violations.Should().BeEmpty(
            "tenant economic brand/instrument strings must not leak into the " +
            "executable code of the tenant-agnostic node mechanism. Violations:\n" +
            string.Join("\n", violations));
    }

    /// <summary>
    /// Removes C# block comments (<c>/* … */</c>, which subsumes <c>/// </c> and
    /// <c>//</c> XML/doc lines) and single-line comments so the scan inspects
    /// active code only. String literals are left intact — a brand string in a
    /// literal IS a leak and must be caught.
    /// </summary>
    private static string StripComments(string source)
    {
        // Block comments first (covers multi-line /* */), then line comments
        // (covers // and /// doc comments). Ordering matters so a // inside a
        // block comment is not double-handled, but for leak-detection purposes
        // removing both in sequence is sufficient and conservative.
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var sb = new StringBuilder(noBlock.Length);
        foreach (var line in noBlock.Split('\n'))
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            sb.Append(idx >= 0 ? line[..idx] : line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Walks up from the test assembly's base directory to the repository root —
    /// the first ancestor that contains a <c>Services/Quest/Handlers</c> folder.
    /// Throws a clear message if it cannot be found (never silently returns a
    /// bad path that would make the scan vacuously pass).
    /// </summary>
    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var marker = Path.Combine(dir.FullName, "Services", "Quest", "Handlers");
            if (Directory.Exists(marker))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (no ancestor of " +
            $"'{AppContext.BaseDirectory}' contains a 'Services/Quest/Handlers' " +
            "directory). The brand-leak guard cannot run — fix path resolution " +
            "rather than letting the scan pass on an empty set.");
    }
}
