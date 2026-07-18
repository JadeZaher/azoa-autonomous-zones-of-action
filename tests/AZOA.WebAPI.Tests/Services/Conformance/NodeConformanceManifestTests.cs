using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Services.Conformance;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Services.Conformance;

public sealed class NodeConformanceManifestTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), "azoa-conformance-", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Manifest_FromCurrentGateArtifacts_RoundTripsOfflineVerifier()
    {
        var service = CreateService(new FixedTimeProvider(DateTimeOffset.Parse("2026-07-13T12:00:00Z")));

        var availability = await service.TryGetDocumentAsync();

        availability.IsAvailable.Should().BeTrue();
        var document = availability.Document ?? throw new Xunit.Sdk.XunitException("Expected a conformance document.");
        NodeConformanceVerifier.TryVerify(
            document,
            DateTimeOffset.Parse("2026-07-13T12:01:00Z"),
            out var failure).Should().BeTrue();
        failure.Should().Be(default(NodeConformanceVerificationFailure));
        document.Manifest.Evidence.Select(item => item.Gate)
            .Should().ContainInOrder("G1", "G2", "G3", "G5", "G7");
    }

    [Fact]
    public async Task Verifier_FailsClosedOnTamperExpiryAndUnsupportedVersion()
    {
        var service = CreateService(new FixedTimeProvider(DateTimeOffset.Parse("2026-07-13T12:00:00Z")));
        var document = (await service.TryGetDocumentAsync()).Document!;

        var tampered = document with { Descriptor = document.Descriptor with { NodeId = "other-node" } };
        NodeConformanceVerifier.TryVerify(tampered, document.Manifest.IssuedAt, out var tamperFailure)
            .Should().BeFalse();
        tamperFailure.Should().Be(NodeConformanceVerificationFailure.InvalidSignature);

        NodeConformanceVerifier.TryVerify(document, document.Manifest.ExpiresAt, out var expiryFailure)
            .Should().BeFalse();
        expiryFailure.Should().Be(NodeConformanceVerificationFailure.Expired);

        NodeConformanceVerifier.TryVerify(document with { SchemaVersion = 2 }, document.Manifest.IssuedAt, out var versionFailure)
            .Should().BeFalse();
        versionFailure.Should().Be(NodeConformanceVerificationFailure.UnsupportedVersion);
    }

    [Fact]
    public async Task Rotation_RequiresPriorKeyContinuityAndRestoredKeyStoragePreservesIdentity()
    {
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        var options = CreateOptions();
        var protectionPath = Path.Combine(_temporaryDirectory, "protection");
        var firstProvider = DataProtectionProvider.Create(
            new DirectoryInfo(protectionPath), builder => builder.SetApplicationName("azoa-test-conformance"));
        var keys = new ProtectedFileNodeIdentityKeyService(firstProvider, Options.Create(options));
        var evidence = new StubEvidenceSource();
        var firstService = new NodeConformanceManifestService(evidence, keys, Options.Create(options), new FixedTimeProvider(now));
        var first = (await firstService.TryGetDocumentAsync()).Document!;

        keys.Rotate().Dispose();
        var second = (await firstService.TryGetDocumentAsync()).Document!;
        NodeConformanceVerifier.TryVerify(second, now, out var rotationFailure, first).Should().BeTrue();
        rotationFailure.Should().Be(default(NodeConformanceVerificationFailure));

        var discontinuous = second with { Descriptor = second.Descriptor with { PreviousKey = null } };
        NodeConformanceVerifier.TryVerify(discontinuous, now, out var discontinuityFailure, first).Should().BeFalse();
        discontinuityFailure.Should().Be(NodeConformanceVerificationFailure.KeyDiscontinuity);

        var restoredProvider = DataProtectionProvider.Create(
            new DirectoryInfo(protectionPath), builder => builder.SetApplicationName("azoa-test-conformance"));
        using var restored = new ProtectedFileNodeIdentityKeyService(restoredProvider, Options.Create(options)).GetCurrent();
        restored.Descriptor.CurrentKey.KeyId.Should().Be(second.Descriptor.CurrentKey.KeyId);
    }

    [Fact]
    public async Task Manifest_RefusesToPublishWhenBoundedPayloadLimitIsExceeded()
    {
        var options = CreateOptions();
        options.MaxPayloadBytes = 1;
        var service = CreateService(new FixedTimeProvider(DateTimeOffset.Parse("2026-07-13T12:00:00Z")), options);

        (await service.TryGetDocumentAsync()).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Manifest_CapsExpiryAtEvidenceBoundaryAndRefusesExpiredEvidence()
    {
        var issuedAt = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        var options = CreateOptions();
        options.ManifestLifetimeMinutes = 60;
        var evidenceExpiresAt = issuedAt.AddMinutes(5);
        var boundedService = CreateService(
            new FixedTimeProvider(issuedAt),
            options,
            new StubEvidenceSource(evidenceExpiresAt));

        var boundedDocument = (await boundedService.TryGetDocumentAsync()).Document;
        boundedDocument.Should().NotBeNull();
        boundedDocument!.Manifest.ExpiresAt.Should().Be(evidenceExpiresAt);

        var expiredService = CreateService(
            new FixedTimeProvider(issuedAt),
            CreateOptions(),
            new StubEvidenceSource(issuedAt));
        (await expiredService.TryGetDocumentAsync()).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task TrxEvidenceSource_DerivesMetadataBoundDigestAndRefusesFailedGate()
    {
        var evidenceDirectory = Path.Combine(_temporaryDirectory, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        await WriteEvidenceBundleAsync(evidenceDirectory, now);

        var source = new TrxNodeConformanceEvidenceSource(
            Options.Create(CreateEvidenceOptions(evidenceDirectory)), new FixedTimeProvider(now));
        var evidence = await source.TryReadAsync();

        evidence.Should().NotBeNull();
        var resolvedEvidence = evidence!.Evidence;
        resolvedEvidence.Should().HaveCount(5);
        var g1Path = Path.Combine(evidenceDirectory, "G1.trx");
        var expectedDigest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(g1Path)));
        resolvedEvidence.Single(item => item.Gate == "G1").ArtifactSha256.Should().Be(expectedDigest);

        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "G3.trx"), Trx("G3", "Failed", G3Tests));
        (await source.TryReadAsync()).Should().BeNull();

        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "G3.trx"), Trx("G3", "Passed", [G3Tests[0]]));
        await WriteMetadataAsync(evidenceDirectory, now);
        (await source.TryReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TrxEvidenceSource_FailsClosedWhenMetadataBindingOrFreshnessIsInvalid()
    {
        var evidenceDirectory = Path.Combine(_temporaryDirectory, "evidence-binding");
        Directory.CreateDirectory(evidenceDirectory);
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        var source = new TrxNodeConformanceEvidenceSource(
            Options.Create(CreateEvidenceOptions(evidenceDirectory)), new FixedTimeProvider(now));

        await WriteEvidenceBundleAsync(evidenceDirectory, now, repository: "other/repository");
        (await source.TryReadAsync()).Should().BeNull();

        await WriteEvidenceBundleAsync(evidenceDirectory, now, generatedAt: now.AddMinutes(-24 * 60 - 1));
        (await source.TryReadAsync()).Should().BeNull();

        await WriteEvidenceBundleAsync(evidenceDirectory, now, commit: new string('0', 40));
        (await source.TryReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TrxEvidenceSource_RequiresExactDeclaredArtifactSetAndHashes()
    {
        var evidenceDirectory = Path.Combine(_temporaryDirectory, "evidence-artifacts");
        Directory.CreateDirectory(evidenceDirectory);
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        var source = new TrxNodeConformanceEvidenceSource(
            Options.Create(CreateEvidenceOptions(evidenceDirectory)), new FixedTimeProvider(now));

        await WriteEvidenceBundleAsync(evidenceDirectory, now, files: [
            new NodeConformanceEvidenceFile("G2.trx", new string('a', 64)),
            new NodeConformanceEvidenceFile("G1.trx", new string('a', 64)),
            new NodeConformanceEvidenceFile("G3.trx", new string('a', 64)),
            new NodeConformanceEvidenceFile("G5.trx", new string('a', 64)),
            new NodeConformanceEvidenceFile("G7.trx", new string('a', 64)),
        ]);
        (await source.TryReadAsync()).Should().BeNull();

        await WriteEvidenceBundleAsync(evidenceDirectory, now);
        await File.AppendAllTextAsync(Path.Combine(evidenceDirectory, "G1.trx"), " ");
        (await source.TryReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TrxEvidenceSource_FailsClosedOnOversizedEvidenceAndProhibitedDtd()
    {
        var evidenceDirectory = Path.Combine(_temporaryDirectory, "evidence-limits");
        Directory.CreateDirectory(evidenceDirectory);
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        var source = new TrxNodeConformanceEvidenceSource(
            Options.Create(CreateEvidenceOptions(evidenceDirectory)), new FixedTimeProvider(now));

        await WriteEvidenceBundleAsync(evidenceDirectory, now);
        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "metadata.json"), new string(' ', 64 * 1024 + 1));
        (await source.TryReadAsync()).Should().BeNull();

        await WriteEvidenceBundleAsync(evidenceDirectory, now);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDirectory, "G1.trx"),
            Trx("G1", "Passed") + new string(' ', 512 * 1024));
        (await source.TryReadAsync()).Should().BeNull();

        await WriteEvidenceBundleAsync(evidenceDirectory, now);
        foreach (var gate in Gates)
            await File.AppendAllTextAsync(Path.Combine(evidenceDirectory, $"{gate}.trx"), new string(' ', 450 * 1024));
        await WriteMetadataAsync(evidenceDirectory, now);
        (await source.TryReadAsync()).Should().BeNull();

        await WriteEvidenceBundleAsync(evidenceDirectory, now);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDirectory, "G1.trx"),
            "<!DOCTYPE TestRun [<!ELEMENT TestRun ANY>]><TestRun><Results><UnitTestResult testName=\"AZOA.WebAPI.IntegrationTests.Gates.G1_CrashDurabilityTest.Evidence\" outcome=\"Passed\" /></Results></TestRun>");
        (await source.TryReadAsync()).Should().BeNull();
    }

    [Fact]
    public void CanonicalSerializer_UsesStableSortedProtocolBytes()
    {
        var document = new NodeConformanceDocument(
            1,
            new NodeDescriptor("node-a", new NodePublicKey("ECDSA_P256_SHA256", "sha256:key", "AQID"), null),
            new NodeConformanceManifest(
                1,
                DateTimeOffset.Parse("2026-07-13T12:00:00Z"),
                DateTimeOffset.Parse("2026-07-13T13:00:00Z"),
                [
                    new NodeConformanceGateEvidence("G7", new string('a', 64), 2),
                    new NodeConformanceGateEvidence("G1", new string('b', 64), 1),
                ],
                "signature"));

        var json = System.Text.Encoding.UTF8.GetString(NodeConformanceCanonicalizer.SerializeDocument(document));

        json.Should().Be("{\"schemaVersion\":1,\"descriptor\":{\"nodeId\":\"node-a\",\"currentKey\":{\"algorithm\":\"ECDSA_P256_SHA256\",\"keyId\":\"sha256:key\",\"subjectPublicKeyInfo\":\"AQID\"}},\"manifest\":{\"schemaVersion\":1,\"issuedAt\":\"2026-07-13T12:00:00.0000000Z\",\"expiresAt\":\"2026-07-13T13:00:00.0000000Z\",\"evidence\":[{\"gate\":\"G1\",\"artifactSha256\":\"" + new string('b', 64) + "\",\"passedTestCount\":1},{\"gate\":\"G7\",\"artifactSha256\":\"" + new string('a', 64) + "\",\"passedTestCount\":2}],\"signature\":\"signature\"}}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
            Directory.Delete(_temporaryDirectory, recursive: true);
    }

    private NodeConformanceManifestService CreateService(
        TimeProvider clock,
        NodeConformanceOptions? options = null,
        INodeConformanceEvidenceSource? evidence = null)
    {
        options ??= CreateOptions();
        var protector = new EphemeralDataProtectionProvider();
        var keys = new ProtectedFileNodeIdentityKeyService(protector, Options.Create(options));
        return new NodeConformanceManifestService(evidence ?? new StubEvidenceSource(), keys, Options.Create(options), clock);
    }

    private NodeConformanceOptions CreateOptions() => new()
    {
        Enabled = true,
        NodeId = "azoa-test-node",
        KeyStoragePath = Path.Combine(_temporaryDirectory, "identity"),
        EvidenceDirectory = Path.Combine(_temporaryDirectory, "evidence"),
        ExpectedRepository = "JadeZaher/azoa-autonomous-zones-of-action",
        ExpectedWorkflow = "JadeZaher/azoa-autonomous-zones-of-action/.github/workflows/ci.yml@refs/heads/main",
        MaxEvidenceAgeMinutes = 24 * 60,
        ManifestLifetimeMinutes = 60,
        MaxPayloadBytes = 16 * 1024,
    };

    private static readonly string[] Gates = ["G1", "G2", "G3", "G5", "G7"];
    private static readonly string[] G3Tests =
    [
        "AZOA.WebAPI.IntegrationTests.Gates.G3_InjectionSuiteTest.G3_ControllerPaths_HostileInput_LandsAsLiteralNotSurrealQl",
        "AZOA.WebAPI.IntegrationTests.Gates.G3_InjectionSuiteTest.G3_DirectWithParam_HostileInput_PersistsAsLiteralString",
    ];

    private static string Trx(string gate, string outcome, IReadOnlyList<string>? testNames = null, string? testClassGate = null)
    {
        var testClass = testClassGate ?? gate;
        var resolvedTestNames = testNames ??
            [$"AZOA.WebAPI.IntegrationTests.Gates.{testClass}_{TestClassSuffix(testClass)}.Evidence"];
        return $"<TestRun><Results>{string.Concat(resolvedTestNames.Select(testName =>
            $"<UnitTestResult testName=\"{testName}\" outcome=\"{outcome}\" />"))}</Results></TestRun>";
    }

    private static NodeConformanceOptions CreateEvidenceOptions(string evidenceDirectory) => new()
    {
        EvidenceDirectory = evidenceDirectory,
        ExpectedRepository = "JadeZaher/azoa-autonomous-zones-of-action",
        ExpectedWorkflow = "JadeZaher/azoa-autonomous-zones-of-action/.github/workflows/ci.yml@refs/heads/main",
        MaxEvidenceAgeMinutes = 24 * 60,
    };

    private static async Task WriteEvidenceBundleAsync(
        string evidenceDirectory,
        DateTimeOffset now,
        DateTimeOffset? generatedAt = null,
        string? repository = null,
        string? commit = null,
        IReadOnlyList<NodeConformanceEvidenceFile>? files = null)
    {
        foreach (var gate in Gates)
        {
            var testNames = string.Equals(gate, "G3", StringComparison.Ordinal) ? G3Tests : null;
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, $"{gate}.trx"), Trx(gate, "Passed", testNames));
        }

        await WriteMetadataAsync(evidenceDirectory, now, generatedAt, repository, commit, files);
    }

    private static async Task WriteMetadataAsync(
        string evidenceDirectory,
        DateTimeOffset now,
        DateTimeOffset? generatedAt = null,
        string? repository = null,
        string? commit = null,
        IReadOnlyList<NodeConformanceEvidenceFile>? files = null)
    {
        var metadata = new NodeConformanceEvidenceMetadata(
            "azoa-node-conformance-evidence/v1",
            repository ?? "JadeZaher/azoa-autonomous-zones-of-action",
            commit ?? RuntimeSourceRevision(),
            "JadeZaher/azoa-autonomous-zones-of-action/.github/workflows/ci.yml@refs/heads/main",
            "123456",
            generatedAt ?? now.AddMinutes(-1),
            now.AddHours(1),
            files ?? Gates.Select(gate => new NodeConformanceEvidenceFile(
                $"{gate}.trx",
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(evidenceDirectory, $"{gate}.trx")))))).ToArray());
        await File.WriteAllTextAsync(
            Path.Combine(evidenceDirectory, "metadata.json"),
            JsonSerializer.Serialize(metadata));
    }

    private static string RuntimeSourceRevision()
    {
        var informationalVersion = typeof(TrxNodeConformanceEvidenceSource).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var separator = informationalVersion?.LastIndexOf('+') ?? -1;
        var revision = separator >= 0 ? informationalVersion![(separator + 1)..] : null;
        return revision is { Length: 40 }
            && revision.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
                ? revision
                : throw new Xunit.Sdk.XunitException("Expected a 40-character source revision in the app assembly.");
    }

    private static string TestClassSuffix(string gate) => gate switch
    {
        "G1" => "CrashDurabilityTest",
        "G2" => "IdempotencyTocTouTest",
        "G3" => "InjectionSuiteTest",
        "G5" => "RestoreDrillTest",
        "G7" => "ReconciliationDrillTest",
        _ => throw new ArgumentOutOfRangeException(nameof(gate)),
    };

    private sealed class StubEvidenceSource(DateTimeOffset? validUntil = null) : INodeConformanceEvidenceSource
    {
        public Task<NodeConformanceEvidenceSnapshot?> TryReadAsync(CancellationToken ct = default)
            => Task.FromResult<NodeConformanceEvidenceSnapshot?>(new(
                Gates.Select(gate => new NodeConformanceGateEvidence(gate, new string('a', 64), 1)).ToArray(),
                validUntil ?? DateTimeOffset.MaxValue));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
