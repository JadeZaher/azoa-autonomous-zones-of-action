using System.Text.RegularExpressions;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Architecture;

/// <summary>Every production reserved-operator comparison is inventoried and tagged (plan §8 C3); see Services/NodeAdmin/AGENTS.md §reserved-id-inventory.</summary>
public sealed partial class ReservedIdentityInventoryTests
{
    private static readonly string[] Tags = ["actor-aware", "legacy-only"];

    [Fact]
    public void Every_reserved_identity_site_is_inventoried_with_a_tag_and_reason()
    {
        var rows = InventoryRows();
        rows.Should().NotBeEmpty("the inventory table must be present");
        rows.Where(row => !Tags.Contains(row.Tag, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(row.Reason) || row.Sites <= 0)
            .Select(row => $"{row.File}: tag '{row.Tag}', sites {row.Sites}")
            .Should().BeEmpty("each row needs a positive site count, an actor-aware/legacy-only tag and a reason");

        var declared = rows.GroupBy(row => row.File, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Sites), StringComparer.Ordinal);
        var actual = CSharpSource.Production()
            .Select(source => (source.RelativePath, Sites: source.Code.Split('\n').Count(line => ReservedSite().IsMatch(line))))
            .Where(item => item.Sites > 0)
            .ToDictionary(item => item.RelativePath, item => item.Sites, StringComparer.Ordinal);

        var drift = actual.Keys.Union(declared.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(file => actual.GetValueOrDefault(file) != declared.GetValueOrDefault(file))
            .Select(file => $"{file}: {actual.GetValueOrDefault(file)} site(s) in code, {declared.GetValueOrDefault(file)} inventoried")
            .ToArray();
        string.Join(Environment.NewLine, drift).Should().BeEmpty(
            "a new NodeOperatorIdentity.UserId / ReservedEmail / IsNodeOperator comparison must be tagged in Services/NodeAdmin/AGENTS.md §reserved-id-inventory");
    }

    private sealed record Row(string File, int Sites, string Tag, string Reason);

    private static IReadOnlyList<Row> InventoryRows()
    {
        var text = File.ReadAllText(Path.Combine(CSharpSource.RepositoryRoot(), "Services", "NodeAdmin", "AGENTS.md")).Replace("\r\n", "\n");
        var start = text.IndexOf("## §reserved-id-inventory", StringComparison.Ordinal);
        if (start < 0) return [];
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? text[start..] : text[start..end];
        return section.Split('\n')
            .Select(line => TableRow().Match(line))
            .Where(match => match.Success)
            .Select(match => new Row(match.Groups["file"].Value.Trim(), int.Parse(match.Groups["sites"].Value),
                match.Groups["tag"].Value.Trim(), match.Groups["reason"].Value.Trim()))
            .ToArray();
    }

    [GeneratedRegex(@"NodeOperatorIdentity\.(UserId|ReservedEmail)|\bIsNodeOperator\b")]
    private static partial Regex ReservedSite();

    [GeneratedRegex(@"^\|\s*(?<file>[^|]+\.cs)\s*\|\s*(?<sites>\d+)\s*\|\s*(?<tag>[^|]+)\|\s*(?<reason>.*?)\s*\|\s*$")]
    private static partial Regex TableRow();
}
