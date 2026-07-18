using System.Security.Cryptography;
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
    public async Task TrxEvidenceSource_DerivesDigestAndRefusesFailedGate()
    {
        var evidenceDirectory = Path.Combine(_temporaryDirectory, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        foreach (var gate in Gates)
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, $"{gate}.trx"), Trx(gate, "Passed"));

        var source = new TrxNodeConformanceEvidenceSource(Options.Create(new NodeConformanceOptions
        {
            EvidenceDirectory = evidenceDirectory,
        }));
        var evidence = await source.TryReadAsync();

        evidence.Should().NotBeNull();
        var resolvedEvidence = evidence!;
        resolvedEvidence.Should().HaveCount(5);
        var g1Path = Path.Combine(evidenceDirectory, "G1.trx");
        var expectedDigest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(g1Path)));
        resolvedEvidence.Single(item => item.Gate == "G1").ArtifactSha256.Should().Be(expectedDigest);

        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "G3.trx"), Trx("G3", "Failed"));
        (await source.TryReadAsync()).Should().BeNull();

        await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "G3.trx"), Trx("G3", "Passed", "G2"));
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

    private NodeConformanceManifestService CreateService(TimeProvider clock, NodeConformanceOptions? options = null)
    {
        options ??= CreateOptions();
        var protector = new EphemeralDataProtectionProvider();
        var keys = new ProtectedFileNodeIdentityKeyService(protector, Options.Create(options));
        return new NodeConformanceManifestService(new StubEvidenceSource(), keys, Options.Create(options), clock);
    }

    private NodeConformanceOptions CreateOptions() => new()
    {
        Enabled = true,
        NodeId = "azoa-test-node",
        KeyStoragePath = Path.Combine(_temporaryDirectory, "identity"),
        EvidenceDirectory = Path.Combine(_temporaryDirectory, "evidence"),
        ManifestLifetimeMinutes = 60,
        MaxPayloadBytes = 16 * 1024,
    };

    private static readonly string[] Gates = ["G1", "G2", "G3", "G5", "G7"];

    private static string Trx(string gate, string outcome, string? testClassGate = null)
    {
        var testClass = testClassGate ?? gate;
        return $"<TestRun><Results><UnitTestResult testName=\"AZOA.WebAPI.IntegrationTests.Gates.{testClass}_" +
               $"{TestClassSuffix(testClass)}.Evidence\" outcome=\"{outcome}\" /></Results></TestRun>";
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

    private sealed class StubEvidenceSource : INodeConformanceEvidenceSource
    {
        public Task<IReadOnlyList<NodeConformanceGateEvidence>?> TryReadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NodeConformanceGateEvidence>?>(Gates
                .Select(gate => new NodeConformanceGateEvidence(gate, new string('a', 64), 1))
                .ToArray());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
