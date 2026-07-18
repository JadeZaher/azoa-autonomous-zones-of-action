using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Derives the five required gate facts from CI-produced TRX result files.</summary>
public sealed class TrxNodeConformanceEvidenceSource : INodeConformanceEvidenceSource
{
    private static readonly string[] RequiredGates = ["G1", "G2", "G3", "G5", "G7"];
    private static readonly IReadOnlyDictionary<string, string> GateTestClasses =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["G1"] = "AZOA.WebAPI.IntegrationTests.Gates.G1_CrashDurabilityTest",
            ["G2"] = "AZOA.WebAPI.IntegrationTests.Gates.G2_IdempotencyTocTouTest",
            ["G3"] = "AZOA.WebAPI.IntegrationTests.Gates.G3_InjectionSuiteTest",
            ["G5"] = "AZOA.WebAPI.IntegrationTests.Gates.G5_RestoreDrillTest",
            ["G7"] = "AZOA.WebAPI.IntegrationTests.Gates.G7_ReconciliationDrillTest",
        };
    private readonly IOptions<NodeConformanceOptions> _options;

    public TrxNodeConformanceEvidenceSource(IOptions<NodeConformanceOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NodeConformanceGateEvidence>?> TryReadAsync(CancellationToken ct = default)
    {
        var directory = _options.Value.EvidenceDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        var evidence = new List<NodeConformanceGateEvidence>(RequiredGates.Length);
        foreach (var gate in RequiredGates)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, $"{gate}.trx");
            if (!TryReadPassedGate(path, gate, out var passedTestCount))
                return null;

            await using var stream = File.OpenRead(path);
            var digest = await SHA256.HashDataAsync(stream, ct);
            evidence.Add(new NodeConformanceGateEvidence(gate, Convert.ToHexStringLower(digest), passedTestCount));
        }

        return evidence;
    }

    private static bool TryReadPassedGate(string path, string gate, out int passedTestCount)
    {
        passedTestCount = 0;
        try
        {
            var document = XDocument.Load(path, LoadOptions.None);
            var results = document.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "UnitTestResult", StringComparison.Ordinal))
                .ToArray();
            if (results.Length == 0 || results.Any(result => !string.Equals(
                    (string?)result.Attribute("outcome"), "Passed", StringComparison.Ordinal)))
            {
                return false;
            }

            var expectedClass = GateTestClasses[gate] + ".";
            if (results.Any(result => !((string?)result.Attribute("testName"))
                    ?.StartsWith(expectedClass, StringComparison.Ordinal) == true))
            {
                return false;
            }

            passedTestCount = results.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return false;
        }
    }
}
