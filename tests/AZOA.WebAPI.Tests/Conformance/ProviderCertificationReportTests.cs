using System.Text;
using FluentAssertions;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models;
using Xunit;
using Xunit.Abstractions;

namespace AZOA.WebAPI.Tests.Conformance;

/// <summary>
/// Enrollment, exemption hygiene, and the printed certification report. FR-6 requires a route
/// to pass conformance before it can be enabled, so the certification has to be readable and
/// every gap has to be written down.
/// </summary>
public class ProviderCertificationReportTests
{
    private readonly ITestOutputHelper _output;

    public ProviderCertificationReportTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The enrollment gate. A new provider class joins the battery by existing; if the harness
    /// cannot build it, this fails and names it. There is no opt-in and no opt-out.
    /// </summary>
    [Fact]
    public void Every_shipped_provider_is_enrolled_automatically()
    {
        var discovered = ProviderConformanceCatalog.DiscoverProviderTypes();

        discovered.Should().NotBeEmpty("the application ships blockchain providers");

        var failures = new List<string>();
        foreach (var providerType in discovered)
        {
            try
            {
                var subject = ProviderConformanceCatalog.Bind(providerType);
                subject.ChainType.Should().NotBeNullOrWhiteSpace();
            }
            catch (Exception ex)
            {
                failures.Add($"{providerType.FullName}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty(
            "every provider must be constructible from configuration, a logger, an optional transport "
            + "and an optional signer factory so it inherits the whole battery without opting in");
    }

    [Fact]
    public void Every_provider_reports_a_distinct_chain_type()
    {
        var byChainType = ProviderConformanceCatalog.DiscoverProviderTypes()
            .GroupBy(ProviderConformanceCatalog.ReadChainType, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.Name))}")
            .ToList();

        byChainType.Should().BeEmpty("the factory keys providers by chain type, so two cannot share one");
    }

    /// <summary>
    /// An exemption that names a provider or an invariant that does not exist is a stale
    /// exemption, which is how a conformance suite quietly stops covering anything.
    /// </summary>
    [Fact]
    public void Every_declared_exemption_targets_a_real_provider_and_a_real_invariant()
    {
        var chainTypes = ProviderConformanceCatalog.DiscoverProviderTypes()
            .Select(ProviderConformanceCatalog.ReadChainType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var exemption in ProviderConformanceExemptions.All)
        {
            chainTypes.Should().Contain(
                exemption.ChainType,
                $"exemption for '{exemption.ChainType}' names a provider that is no longer shipped");

            ConformanceInvariant.All.Should().Contain(
                exemption.InvariantId,
                $"exemption for {exemption.ChainType} names an invariant that does not exist");

            exemption.Reason.Should().NotBeNullOrWhiteSpace(
                $"{exemption.ChainType}/{exemption.InvariantId} has no justification — an unexplained "
                + "skip is how a conformance suite becomes decoration");

            exemption.Evidence.Should().NotBeNullOrWhiteSpace(
                $"{exemption.ChainType}/{exemption.InvariantId} names no compensating evidence");
        }
    }

    [Fact]
    public void No_provider_is_exempt_from_more_than_half_the_battery()
    {
        foreach (var group in ProviderConformanceExemptions.All.GroupBy(e => e.ChainType, StringComparer.OrdinalIgnoreCase))
        {
            group.Count().Should().BeLessThan(
                (ConformanceInvariant.All.Count / 2) + 1,
                $"{group.Key} is exempt from most of the battery, which is not a certification");
        }
    }

    /// <summary>
    /// The compensating controls behind every <see cref="ConformanceVerdict.NotApplicable"/>
    /// verdict. An exemption whose compensating control is untested is just a hole.
    /// </summary>
    [Fact]
    public async Task Every_not_applicable_exemption_has_its_compensating_control_asserted()
    {
        foreach (var exemption in ProviderConformanceExemptions.All
                     .Where(e => e.Verdict == ConformanceVerdict.NotApplicable))
        {
            var providerType = ProviderConformanceCatalog.DiscoverProviderTypes()
                .Single(t => string.Equals(
                    ProviderConformanceCatalog.ReadChainType(t), exemption.ChainType, StringComparison.OrdinalIgnoreCase));
            var subject = ProviderConformanceCatalog.Bind(providerType);

            switch (exemption.InvariantId)
            {
                case ConformanceInvariant.NoFabricatedSuccess:
                    await AssertEveryIdentifierIsMarkedSynthetic(subject);
                    break;

                case ConformanceInvariant.BroadcastIsNotRetried:
                    subject.ProviderType.GetConstructors()
                        .SelectMany(c => c.GetParameters())
                        .Any(p => typeof(HttpMessageHandler).IsAssignableFrom(p.ParameterType))
                        .Should().BeFalse(
                            $"{exemption.ChainType} is exempt from C3 because it has no transport, "
                            + "but its constructor set accepts one");
                    subject.Transport.RequestCount.Should().Be(
                        0, $"{exemption.ChainType} issued a network request despite claiming to have no transport");
                    break;

                case ConformanceInvariant.ConfirmationTimeoutIsNotFailure:
                    ILStringLiterals.Contains(subject.ProviderType, OperationStatus.PendingConfirmationMarker)
                        .Should().BeFalse(
                            $"{exemption.ChainType} is exempt from C4 because nothing it does can be pending, "
                            + "but it emits the pending-confirmation marker");
                    break;

                case ConformanceInvariant.PerUserSigningNeverFallsBack:
                    subject.ProviderType.GetConstructors()
                        .SelectMany(c => c.GetParameters())
                        .Any(p => p.ParameterType == typeof(ITransactionSignerFactory))
                        .Should().BeFalse(
                            $"{exemption.ChainType} is exempt from C5 because it resolves no signer, "
                            + "but its constructor set accepts a signer factory");
                    break;

                default:
                    throw new InvalidOperationException(
                        $"{exemption.ChainType}/{exemption.InvariantId} is declared NotApplicable but this test "
                        + "asserts no compensating control for it. Add one, or downgrade the verdict.");
            }
        }
    }

    private static async Task AssertEveryIdentifierIsMarkedSynthetic(ProviderUnderTest subject)
    {
        var amount = AZOA.WebAPI.Core.RailAmount.OfChainAsset(1_000);
        var context = AZOA.WebAPI.Core.SigningContext.Platform;

        var mintResult = await subject.Provider.MintAsync("ipfs://x", amount, "NFT", "conformance-wallet", context);
        var transferResult = await subject.Provider.TransferAsync(
            "conformance-token", "conformance-sender", "conformance-recipient", amount, context);
        var burnResult = await subject.Provider.BurnAsync("conformance-token", amount, "conformance-wallet", context);

        var identifiers = new List<(string Path, string? Id, bool IsError)>
        {
            ("MintAsync", mintResult.Result, mintResult.IsError),
            ("TransferAsync", transferResult.Result, transferResult.IsError),
            ("BurnAsync", burnResult.Result, burnResult.IsError),
        };

        if (subject.Provider.TryGetModule<IBridgeSourceLeg>(out var source) && source is not null)
        {
            var locked = await source.LockAsync(new(
                "conformance-token", "conformance-vault", amount, "Algorand", "conformance-recipient"));
            identifiers.Add(("IBridgeSourceLeg.LockAsync", locked.Result?.TransactionId, locked.IsError));
        }

        if (subject.Provider.TryGetModule<IBridgeTargetLeg>(out var target) && target is not null)
        {
            var minted = await target.MintWrappedAsync(new(
                "Algorand", "conformance-token", "ipfs://x", amount, "conformance-recipient"));
            identifiers.Add(("IBridgeTargetLeg.MintWrappedAsync", minted.Result, minted.IsError));
        }

        foreach (var (path, id, isError) in identifiers.Where(i => !i.IsError))
        {
            id.Should().StartWith(
                "sim:",
                $"{subject.ChainType}.{path} is exempt from C1 only because every identifier it produces is "
                + "marked synthetic and can never be mistaken for a chain identifier");
        }

        // A hash that is NOT marked synthetic must be refused, so a real chain hash can never be
        // reported confirmed by the synthetic provider.
        var realShapedHash = "0xc0f0f0a0ce000000000000000000000000000000000000000000000000000fa1";
        var status = await subject.Provider.GetTransactionStatusAsync(realShapedHash);
        status.IsError.Should().BeTrue(
            $"{subject.ChainType} must refuse a transaction hash that is not marked synthetic");
    }

    /// <summary>
    /// The certification report. Prints one row per (provider, invariant) with its verdict and,
    /// for anything that is not directly certified, the reason and where the decisive evidence
    /// lives. Read the test output to certify a route.
    /// </summary>
    [Fact]
    public void Certification_report()
    {
        var report = new StringBuilder();
        report.AppendLine("PROVIDER BOUNDARY CONFORMANCE — CERTIFICATION REPORT (FR-6)");
        report.AppendLine(new string('=', 78));

        foreach (var providerType in ProviderConformanceCatalog.DiscoverProviderTypes())
        {
            var subject = ProviderConformanceCatalog.Bind(providerType);
            var modules = ProviderConformanceCatalog.DiscoverCapabilityModuleInterfaces()
                .Where(m => m.IsAssignableFrom(providerType))
                .Select(m => m.Name)
                .ToList();

            report.AppendLine();
            report.AppendLine($"{subject.ChainType}  ({providerType.Name})");
            report.AppendLine($"  capability modules : {(modules.Count == 0 ? "none" : string.Join(", ", modules))}");
            report.AppendLine($"  platform material  : {subject.PlatformMaterialPaths ?? "none configured"}");
            report.AppendLine($"  broadcast path     : "
                              + (providerType.GetConstructors().SelectMany(c => c.GetParameters())
                                  .Any(p => p.ParameterType == typeof(ITransactionSignerFactory)) ? "yes" : "no"));

            foreach (var invariant in ConformanceInvariant.All)
            {
                var verdict = ProviderConformanceExemptions.VerdictFor(subject.ChainType, invariant);
                report.AppendLine(
                    $"  {invariant} {ConformanceInvariant.Describe(invariant),-52} {Label(verdict)}");

                if (ProviderConformanceExemptions.TryGet(subject.ChainType, invariant, out var exemption))
                {
                    report.AppendLine($"        why      : {exemption.Reason}");
                    report.AppendLine($"        evidence : {exemption.Evidence}");
                }
            }
        }

        report.AppendLine();
        report.AppendLine(new string('-', 78));
        report.AppendLine(
            $"providers: {ProviderConformanceCatalog.DiscoverProviderTypes().Count}   "
            + $"invariants: {ConformanceInvariant.All.Count}   "
            + $"declared exemptions: {ProviderConformanceExemptions.All.Count}");

        _output.WriteLine(report.ToString());

        // The report is only meaningful if it covers everything.
        var pairings = ProviderConformanceCatalog.DiscoverProviderTypes().Count * ConformanceInvariant.All.Count;
        pairings.Should().BeGreaterThan(0);
    }

    private static string Label(ConformanceVerdict verdict) => verdict switch
    {
        ConformanceVerdict.Certified => "CERTIFIED",
        ConformanceVerdict.CertifiedElsewhere => "CERTIFIED ELSEWHERE (declared)",
        _ => "NOT APPLICABLE (declared)",
    };
}
