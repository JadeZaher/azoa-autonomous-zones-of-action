using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Conformance;

/// <summary>Derives trusted gate facts from metadata-bound CI TRX result files.</summary>
public sealed class TrxNodeConformanceEvidenceSource : INodeConformanceEvidenceSource
{
    private const string MetadataSchema = "azoa-node-conformance-evidence/v1";
    private const int MaxMetadataBytes = 64 * 1024;
    private const int MaxTrxBytes = 512 * 1024;
    private const int MaxEvidenceBytes = 2 * 1024 * 1024;
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
    private static readonly HashSet<string> RequiredG3Tests = new(StringComparer.Ordinal)
    {
        "AZOA.WebAPI.IntegrationTests.Gates.G3_InjectionSuiteTest.G3_ControllerPaths_HostileInput_LandsAsLiteralNotSurrealQl",
        "AZOA.WebAPI.IntegrationTests.Gates.G3_InjectionSuiteTest.G3_DirectWithParam_HostileInput_PersistsAsLiteralString",
    };
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly XmlReaderSettings TrxXmlReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxTrxBytes,
        MaxCharactersFromEntities = 0,
    };

    private readonly IOptions<NodeConformanceOptions> _options;
    private readonly TimeProvider _clock;

    public TrxNodeConformanceEvidenceSource(
        IOptions<NodeConformanceOptions> options,
        TimeProvider? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<NodeConformanceEvidenceSnapshot?> TryReadAsync(CancellationToken ct = default)
    {
        var options = _options.Value;
        var directory = options.EvidenceDirectory;
        if (!TryValidateEvidenceOptions(options) || !Directory.Exists(directory))
            return null;

        try
        {
            ct.ThrowIfCancellationRequested();
            var metadataPath = Path.Combine(directory, "metadata.json");
            var metadataBytes = await TryReadBoundedBytesAsync(metadataPath, MaxMetadataBytes, ct);
            if (metadataBytes is null)
                return null;

            var metadata = JsonSerializer.Deserialize<NodeConformanceEvidenceMetadata>(
                metadataBytes, MetadataJsonOptions);
            if (!TryValidateMetadata(metadata, options, _clock.GetUtcNow(), options.ExpectedSourceRevision))
                return null;

            var evidence = new List<NodeConformanceGateEvidence>(RequiredGates.Length);
            var totalEvidenceBytes = metadataBytes.Length;
            foreach (var gate in RequiredGates)
            {
                ct.ThrowIfCancellationRequested();
                var fileName = $"{gate}.trx";
                var expectedHash = metadata!.Files.Single(file => string.Equals(file.Name, fileName, StringComparison.Ordinal)).Sha256;
                var bytes = await TryReadBoundedBytesAsync(Path.Combine(directory, fileName), MaxTrxBytes, ct);
                if (bytes is null || bytes.Length > MaxEvidenceBytes - totalEvidenceBytes)
                    return null;

                totalEvidenceBytes += bytes.Length;
                if (!TryReadPassedGate(bytes, gate, out var passedTestCount))
                    return null;

                var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (!string.Equals(digest, expectedHash, StringComparison.Ordinal))
                    return null;

                evidence.Add(new NodeConformanceGateEvidence(gate, digest, passedTestCount));
            }

            return new NodeConformanceEvidenceSnapshot(evidence, metadata!.ExpiresAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or XmlException)
        {
            return null;
        }
    }

    private static bool TryValidateEvidenceOptions(NodeConformanceOptions options)
        => !string.IsNullOrWhiteSpace(options.EvidenceDirectory)
            && !string.IsNullOrWhiteSpace(options.ExpectedRepository)
            && !string.IsNullOrWhiteSpace(options.ExpectedWorkflow)
            && !string.IsNullOrWhiteSpace(options.ExpectedSourceRevision)
            && options.MaxEvidenceAgeMinutes is > 0 and <= 24 * 60;

    private static bool TryValidateMetadata(
        NodeConformanceEvidenceMetadata? metadata,
        NodeConformanceOptions options,
        DateTimeOffset now,
        string? expectedSourceRevision)
    {
        if (metadata is null
            || !string.Equals(metadata.Schema, MetadataSchema, StringComparison.Ordinal)
            || !string.Equals(metadata.Repository, options.ExpectedRepository, StringComparison.Ordinal)
            || !string.Equals(metadata.Workflow, options.ExpectedWorkflow, StringComparison.Ordinal)
            || !string.Equals(metadata.Commit, expectedSourceRevision, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(metadata.RunId)
            || metadata.GeneratedAt.Offset != TimeSpan.Zero
            || metadata.ExpiresAt.Offset != TimeSpan.Zero
            || metadata.GeneratedAt > now
            || metadata.ExpiresAt <= now
            || metadata.ExpiresAt <= metadata.GeneratedAt
            || metadata.GeneratedAt < now.AddMinutes(-options.MaxEvidenceAgeMinutes)
            || metadata.ExpiresAt > metadata.GeneratedAt.AddMinutes(options.MaxEvidenceAgeMinutes)
            || metadata.Files is null
            || metadata.Files.Count != RequiredGates.Length)
        {
            return false;
        }

        for (var index = 0; index < RequiredGates.Length; index++)
        {
            var file = metadata.Files[index];
            if (file is null
                || !string.Equals(file.Name, $"{RequiredGates[index]}.trx", StringComparison.Ordinal)
                || !IsLowerHexSha256(file.Sha256))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadPassedGate(byte[] bytes, string gate, out int passedTestCount)
    {
        passedTestCount = 0;
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, TrxXmlReaderSettings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var results = document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "UnitTestResult", StringComparison.Ordinal))
            .ToArray();
        if (results.Length == 0 || results.Any(result => !string.Equals(
                (string?)result.Attribute("outcome"), "Passed", StringComparison.Ordinal)))
        {
            return false;
        }

        var expectedClass = GateTestClasses[gate] + ".";
        var testNames = results.Select(result => (string?)result.Attribute("testName")).ToArray();
        if (testNames.Any(testName => testName?.StartsWith(expectedClass, StringComparison.Ordinal) != true))
            return false;

        if (string.Equals(gate, "G3", StringComparison.Ordinal)
            && (testNames.Length != RequiredG3Tests.Count
                || !RequiredG3Tests.SetEquals(testNames!)))
        {
            return false;
        }

        passedTestCount = results.Length;
        return true;
    }

    private static async Task<byte[]?> TryReadBoundedBytesAsync(string path, int byteLimit, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > byteLimit)
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bytes = new MemoryStream(Math.Min(byteLimit, 81920));
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (true)
            {
                var remaining = byteLimit - (int)bytes.Length;
                if (remaining == 0)
                {
                    if (await stream.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
                        return null;

                    return bytes.ToArray();
                }

                var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct);
                if (read == 0)
                    return bytes.ToArray();

                bytes.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsLowerHexSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

}
