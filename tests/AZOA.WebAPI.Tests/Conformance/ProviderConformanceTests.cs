using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Signing;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Base;
using Xunit;

namespace AZOA.WebAPI.Tests.Conformance;

/// <summary>
/// The provider-boundary conformance battery (FR-6). One shared, provider-agnostic suite that
/// EVERY registered provider is run through: a new chain inherits the whole battery by existing,
/// not by opting in. Per-chain pipelines stay in their own suites; this file certifies the
/// invariants that must hold uniformly. See <c>Conformance/AGENTS.md</c>.
/// </summary>
public class ProviderConformanceTests
{
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> ByChainType = new(() =>
        ProviderConformanceCatalog.DiscoverProviderTypes()
            .ToDictionary(ProviderConformanceCatalog.ReadChainType, t => t, StringComparer.OrdinalIgnoreCase));

    /// <summary>Theory data: every provider the application assembly ships, keyed by chain type.</summary>
    public static IEnumerable<object[]> EnrolledProviders() =>
        ByChainType.Value.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    private static ProviderUnderTest Bind(string chainType) =>
        ProviderConformanceCatalog.Bind(ByChainType.Value[chainType]);

    // ─── C1 — no fabricated success ───

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C1_no_value_path_returns_success_when_the_chain_never_acknowledged(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;

        var outcomes = await InvokeValuePathsAsync(subject, RailAmount.OfChainAsset(1_000), SigningContext.Platform);

        if (ProviderConformanceExemptions.VerdictFor(chainType, ConformanceInvariant.NoFabricatedSuccess)
            == ConformanceVerdict.NotApplicable)
        {
            // The exemption is not a free pass: every identifier must still be marked as
            // synthetic, so nothing it returns can be mistaken for a real chain identifier.
            foreach (var (name, result) in outcomes.Where(o => !o.Result.IsError))
            {
                result.Result.Should().NotBeNullOrWhiteSpace($"{chainType}.{name} returned a successful empty id");
                result.Result!.Should().StartWith(
                    "sim:",
                    $"{chainType}.{name} is exempt from C1 only because every identifier it returns is marked synthetic");
            }
            return;
        }

        foreach (var (name, result) in outcomes)
        {
            result.IsError.Should().BeTrue(
                $"{chainType}.{name} returned Ok('{result.Result}') although the chain acknowledged nothing — "
                + "a transaction id must come from the chain or the signer, never a generator");
        }
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C1_bridge_legs_never_fabricate_a_settlement(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;
        var exempt = ProviderConformanceExemptions.VerdictFor(chainType, ConformanceInvariant.NoFabricatedSuccess)
            == ConformanceVerdict.NotApplicable;

        if (subject.Provider.TryGetModule<IBridgeSourceLeg>(out var source) && source is not null)
        {
            var locked = await source.LockAsync(new(
                TokenId: "conformance-token", VaultAddress: "conformance-vault",
                Amount: RailAmount.OfChainAsset(1_000), TargetChain: "Simulated",
                TargetRecipient: "conformance-recipient"));
            AssertNoFabrication(chainType, "IBridgeSourceLeg.LockAsync", locked.IsError, locked.Result?.TransactionId, exempt);

            var released = await source.ReleaseAsync(new(
                TokenId: "conformance-token", VaultAddress: "conformance-vault",
                RecipientAddress: "conformance-recipient", Amount: RailAmount.OfChainAsset(1_000)));
            AssertNoFabrication(chainType, "IBridgeSourceLeg.ReleaseAsync", released.IsError, released.Result, exempt);
        }

        if (subject.Provider.TryGetModule<IBridgeTargetLeg>(out var target) && target is not null)
        {
            var minted = await target.MintWrappedAsync(new(
                SourceChain: "Algorand", SourceTokenId: "conformance-token", TokenUri: "ipfs://conformance",
                Amount: RailAmount.OfChainAsset(1_000), RecipientAddress: "conformance-recipient"));
            AssertNoFabrication(chainType, "IBridgeTargetLeg.MintWrappedAsync", minted.IsError, minted.Result, exempt);

            var burned = await target.BurnWrappedAsync(new(
                TokenId: "conformance-token", WalletAddress: "conformance-wallet",
                Amount: RailAmount.OfChainAsset(1_000), SourceChain: "Algorand",
                SourceRecipient: "conformance-recipient"));
            AssertNoFabrication(chainType, "IBridgeTargetLeg.BurnWrappedAsync", burned.IsError, burned.Result, exempt);
        }
    }

    private static void AssertNoFabrication(string chainType, string leg, bool isError, string? id, bool exempt)
    {
        if (isError) return;

        if (exempt)
        {
            id.Should().StartWith("sim:", $"{chainType}.{leg} may only succeed with a marked synthetic identifier");
            return;
        }

        isError.Should().BeTrue(
            $"{chainType}.{leg} returned Ok('{id}') with no chain acknowledgement — "
            + "a bridge leg must broadcast a real transaction or fail loud");
    }

    // ─── C2 — unknown is never confirmed ───

    /// <summary>A well-formed 32-byte hash that was never broadcast on any chain.</summary>
    private const string NeverBroadcastTxHash =
        "0xc0f0f0a0ce000000000000000000000000000000000000000000000000000fa1";

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C2_an_unknown_transaction_reads_as_unknown_not_confirmed(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.NotFound;

        var confirmation = await subject.Provider.GetTransactionConfirmationAsync(NeverBroadcastTxHash);

        confirmation.Result.Should().Be(
            ChainConfirmation.Unknown,
            $"{chainType} must answer Unknown for a transaction it has never observed — "
            + "anything else lets a reconciler act on a transaction that does not exist");
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C2_the_vacuous_null_payload_is_never_read_as_confirmed(string chainType)
    {
        // HTTP 200 carrying `result: null`. This is the exact shape that made
        // `receipt?.Err == null` evaluate true for a receipt that does not exist.
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.NullPayload;

        var confirmation = await subject.Provider.GetTransactionConfirmationAsync(NeverBroadcastTxHash);

        confirmation.Result.Should().NotBe(
            ChainConfirmation.Confirmed,
            $"{chainType} read a null payload as a confirmation — the vacuous-null trap "
            + "(a null receipt has no error field, which is not the same as having no error)");
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C2_a_refusing_node_is_never_read_as_confirmed(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;

        var confirmation = await subject.Provider.GetTransactionConfirmationAsync(NeverBroadcastTxHash);
        var status = await subject.Provider.GetTransactionStatusAsync(NeverBroadcastTxHash);

        confirmation.Result.Should().NotBe(
            ChainConfirmation.Confirmed, $"{chainType} confirmed a transaction while its node was refusing");
        ChainTxClassifier.Classify(status).Should().NotBe(
            ChainConfirmation.Confirmed, $"{chainType} status of an unknown transaction classified as Confirmed");
    }

    // ─── C3 — a signed transaction is never automatically resubmitted ───

    [Fact]
    public async Task C3_the_shared_broadcast_seam_never_resubmits_after_an_ambiguous_failure()
    {
        // Ambiguous post-send failure: the node may already hold the transaction.
        var probe = ConformanceProbeProvider.Create();
        int invocations = 0;

        var ambiguous = await Assert.ThrowsAsync<HttpRequestException>(() =>
            probe.RunBroadcastAsync<string>(() =>
            {
                invocations++;
                throw new HttpRequestException("gateway timeout", null, HttpStatusCode.GatewayTimeout);
            }));

        ambiguous.Should().NotBeNull();
        invocations.Should().Be(1, "an irreversible broadcast is never auto-retried on an ambiguous failure");
    }

    [Fact]
    public async Task C3_the_shared_broadcast_seam_retries_only_a_provably_pre_broadcast_failure()
    {
        var probe = ConformanceProbeProvider.Create();
        int invocations = 0;

        var result = await probe.RunBroadcastAsync<string>(() =>
        {
            invocations++;
            if (invocations == 1)
                throw new HttpRequestException(
                    "connection refused", new SocketException((int)SocketError.ConnectionRefused));
            return Task.FromResult("broadcast");
        });

        result.Should().Be("broadcast");
        invocations.Should().Be(2, "a request that never reached the node can safely be re-sent");
    }

    [Fact]
    public async Task C3_the_idempotent_seam_is_still_allowed_to_retry()
    {
        var probe = ConformanceProbeProvider.Create();
        int invocations = 0;

        var result = await probe.RunIdempotentAsync<string>(() =>
        {
            invocations++;
            if (invocations < 3)
                throw new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway);
            return Task.FromResult("read");
        });

        result.Should().Be("read");
        invocations.Should().Be(3, "a read may be retried; only broadcasts are one-shot");
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C3_a_signed_payload_never_appears_on_the_wire_twice(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;

        await InvokeValuePathsAsync(subject, RailAmount.OfChainAsset(1_000), SigningContext.Platform);

        foreach (var encoding in subject.Signer.WireEncodings())
        {
            subject.Transport.RequestsCarrying(encoding).Should().BeLessOrEqualTo(
                1, $"{chainType} put the same signed payload on the wire more than once");
        }

        if (subject.Signer.SignCount == 0)
        {
            ProviderConformanceExemptions.VerdictFor(chainType, ConformanceInvariant.BroadcastIsNotRetried)
                .Should().NotBe(
                    ConformanceVerdict.Certified,
                    $"{chainType} never reached signing under the shared transport, so this assertion is not "
                    + "decisive; declare the verdict in ProviderConformanceExemptions with the suite that is");
        }
    }

    // ─── C4 — a confirmation timeout is success carrying a pending marker ───

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public void C4_a_provider_with_a_broadcast_path_carries_the_pending_marker_contract(string chainType)
    {
        var providerType = ByChainType.Value[chainType];
        var hasBroadcastPath = AcceptsSignerFactory(providerType);
        var carriesMarker = ILStringLiterals.Contains(providerType, OperationStatus.PendingConfirmationMarker);

        if (hasBroadcastPath)
        {
            carriesMarker.Should().BeTrue(
                $"{chainType} can broadcast, so a confirmation timeout must return success carrying "
                + $"'{OperationStatus.PendingConfirmationMarker}' plus the real id — not a failure");
            return;
        }

        ProviderConformanceExemptions.VerdictFor(chainType, ConformanceInvariant.ConfirmationTimeoutIsNotFailure)
            .Should().Be(
                ConformanceVerdict.NotApplicable,
                $"{chainType} has no broadcast path, which must be declared rather than silently skipped");
        carriesMarker.Should().BeFalse(
            $"{chainType} cannot broadcast, so emitting the pending marker would claim a settlement state "
            + "it never occupies");
    }

    // ─── C5 — per-user signing never falls back to a platform key ───

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C5_a_per_user_context_without_custody_never_signs_or_succeeds(string chainType)
    {
        // Platform key material IS configured; the custody seam is NOT wired. The only way to
        // succeed here is to sign a user's operation with the platform key.
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;
        var perUser = new SigningContext(Guid.NewGuid(), Guid.NewGuid(), IsPlatform: false, ChainType: chainType);

        var outcomes = await InvokeValuePathsAsync(subject, RailAmount.OfChainAsset(1_000), perUser);

        if (ProviderConformanceExemptions.VerdictFor(chainType, ConformanceInvariant.PerUserSigningNeverFallsBack)
            == ConformanceVerdict.NotApplicable)
        {
            // The provider resolves no key at all, so there is no platform key to fall back TO.
            // The exemption is still paid for: nothing may be signed, and any success must carry
            // the synthetic marker so it cannot be mistaken for a settled user operation.
            subject.Signer.SignCount.Should().Be(
                0, $"{chainType} is exempt from C5 only because it signs nothing");

            foreach (var (name, result) in outcomes.Where(o => !o.Result.IsError))
            {
                result.Result.Should().StartWith(
                    "sim:",
                    $"{chainType}.{name} accepted a per-user context, which is only tolerable because the "
                    + "identifier it returns is marked synthetic and no key was involved");
            }

            return;
        }

        foreach (var (name, result) in outcomes)
        {
            result.IsError.Should().BeTrue(
                $"{chainType}.{name} settled a per-user operation with no custody seam wired — "
                + "the only key available was the platform key");
        }

        subject.Signer.SignCount.Should().Be(
            0, $"{chainType} produced a signature for a user operation it could not resolve a user key for");
    }

    // ─── C6 — amounts round-trip without truncation ───

    [Fact]
    public void C6_rail_amount_round_trips_past_every_historical_ceiling()
    {
        foreach (var baseUnits in new[]
                 {
                     ConformanceAmounts.AboveDoublePrecision,
                     ConformanceAmounts.UlongCeiling,
                     ConformanceAmounts.AboveUlongCeiling,
                     ConformanceAmounts.Uint256Shaped,
                 })
        {
            var amount = RailAmount.OfChainAsset(baseUnits);
            amount.BaseUnits.Should().Be(baseUnits);
            amount.ToBaseUnitString().Should().Be(baseUnits.ToString());

            RailAmount.TryParse(amount.ToBaseUnitString(), ValueDimension.NativeChainAsset, out var reparsed)
                .Should().BeTrue();
            reparsed.BaseUnits.Should().Be(baseUnits, "a RailAmount must survive a string round trip exactly");
        }
    }

    [Fact]
    public void C6_the_shared_narrowing_seam_refuses_rather_than_truncates()
    {
        var probe = ConformanceProbeProvider.Create();

        probe.Narrow(RailAmount.OfChainAsset(ConformanceAmounts.UlongCeiling), out var atCeiling)
            .Should().BeNull("the ceiling itself is representable");
        atCeiling.Should().Be(ulong.MaxValue);

        var refusal = probe.Narrow(RailAmount.OfChainAsset(ConformanceAmounts.AboveUlongCeiling), out var wrapped);
        refusal.Should().NotBeNull("18.5 ETH in wei exceeds a 64-bit surface and must be refused");
        refusal!.IsError.Should().BeTrue();
        refusal.Message.Should().Contain("Refusing to truncate");
        wrapped.Should().Be(0, "a refused narrowing must never hand back a wrapped count");

        probe.Narrow(RailAmount.OfUsdMinorUnits(1_000), out _)
            .Should().NotBeNull("a fiat-denominated amount is not chain base units and must be refused");
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C6_an_unsettleable_amount_is_refused_before_any_network_call(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;

        var outcomes = await InvokeValuePathsAsync(subject, RailAmount.OfUsdMinorUnits(2_500), SigningContext.Platform);

        foreach (var (name, result) in outcomes)
            result.IsError.Should().BeTrue($"{chainType}.{name} accepted an amount denominated in fiat");

        subject.Transport.RequestCount.Should().Be(
            0, $"{chainType} contacted its node for an amount it cannot settle; the dimension check must come first");
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C6_an_amount_above_the_old_ulong_ceiling_is_never_silently_narrowed(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;

        var outcomes = await InvokeValuePathsAsync(
            subject, RailAmount.OfChainAsset(ConformanceAmounts.AboveUlongCeiling), SigningContext.Platform);

        foreach (var (name, result) in outcomes)
        {
            result.IsError.Should().BeTrue(
                $"{chainType}.{name} reported success for {ConformanceAmounts.AboveUlongCeiling} base units "
                + "with no chain acknowledgement");

            // A 64-bit-native chain must say so; a uint256-native chain must not invent a ceiling.
            if (result.Message.Contains("ceiling", StringComparison.OrdinalIgnoreCase))
            {
                result.Message.Should().Contain(
                    "Refusing to truncate",
                    $"{chainType}.{name} narrowed an amount without naming the refusal");
            }
        }
    }

    // ─── C7 — capability honesty ───

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public void C7_a_provider_exposes_no_method_for_a_capability_it_does_not_implement(string chainType)
    {
        var subject = Bind(chainType);
        var providerMethods = subject.ProviderType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var moduleInterface in ProviderConformanceCatalog.DiscoverCapabilityModuleInterfaces())
        {
            if (moduleInterface.IsAssignableFrom(subject.ProviderType))
                continue;

            var capabilityMethods = CapabilityMethodNames(moduleInterface);
            var leaked = capabilityMethods.Where(providerMethods.Contains).ToList();

            leaked.Should().BeEmpty(
                $"{chainType} does not implement {moduleInterface.Name}, so it must expose no method that "
                + $"could fabricate that capability's success; found {string.Join(", ", leaked)}. "
                + "Absence IS the fail-closed refusal (Providers/Blockchain/AGENTS.md §bridge).");
        }
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public void C7_no_provider_carries_a_proof_verification_method(string chainType)
    {
        // A provider-level verifier previously returned true unconditionally. Verification
        // belongs to the protocol path and only there.
        var forbidden = new[] { "VerifyBridgeProofAsync", "VerifyProofAsync", "VerifyVAAAsync", "VerifyVaaAsync" };
        var declared = ByChainType.Value[chainType]
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(forbidden.Contains)
            .ToList();

        declared.Should().BeEmpty(
            $"{chainType} declares a proof verifier; the single verification path is "
            + "WormholeProtocol → WormholeAdapter → Secp256k1VaaSignatureVerifier and it is fail-closed");
    }

    // ─── C8 — fail-closed errors name the real cause ───

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C8_a_transport_failure_is_never_blamed_on_the_signer(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;
        var signerIsResolvable = subject.SignerFactory.TryGetSigner(chainType, out var resolved) && resolved is not null;

        var outcomes = await InvokeValuePathsAsync(subject, RailAmount.OfChainAsset(1_000), SigningContext.Platform);

        foreach (var (name, result) in outcomes.Where(o => o.Result.IsError))
        {
            result.Message.Should().NotBeNullOrWhiteSpace($"{chainType}.{name} failed closed without saying why");

            if (!signerIsResolvable)
                continue;

            foreach (var misattribution in new[]
                     {
                         "no signer", "no transaction signer", "signer not registered",
                         "signer is not registered", "signer unavailable",
                     })
            {
                result.Message.Should().NotContainEquivalentOf(
                    misattribution,
                    $"{chainType}.{name} blamed the signer for a failure that was not the signer's — "
                    + "a signer for this chain is registered and was handed to the provider");
            }
        }
    }

    [Theory]
    [MemberData(nameof(EnrolledProviders))]
    public async Task C8_an_unreachable_node_is_an_error_and_never_a_zero_balance(string chainType)
    {
        var subject = Bind(chainType);
        subject.Transport.Mode = ConformanceTransport.ResponseMode.RefuseEverything;

        if (!subject.ProviderType.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => typeof(HttpMessageHandler).IsAssignableFrom(p.ParameterType)))
        {
            // No transport at all: there is no unreachable node to misreport.
            return;
        }

        var balance = await subject.Provider.GetBalanceAsync("conformance-address", tokenId: null);

        balance.IsError.Should().BeTrue(
            $"{chainType} answered a balance query while its node was refusing; "
            + "an unreachable rail is an error, never a zero balance");
    }

    // ─── Shared invocation helper ───

    private static async Task<IReadOnlyList<(string Name, AZOAResult<string> Result)>> InvokeValuePathsAsync(
        ProviderUnderTest subject, RailAmount amount, SigningContext context)
    {
        var provider = subject.Provider;
        return new[]
        {
            ("MintAsync", await provider.MintAsync(
                "ipfs://conformance", amount, "NFT", "conformance-wallet", context)),
            ("BurnAsync", await provider.BurnAsync(
                "conformance-token", amount, "conformance-wallet", context)),
            ("TransferAsync", await provider.TransferAsync(
                "conformance-token", "conformance-sender", "conformance-recipient", amount, context)),
        };
    }

    private static bool AcceptsSignerFactory(Type providerType) =>
        providerType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == typeof(ITransactionSignerFactory));

    /// <summary>
    /// The method names a capability module contributes, excluding the identity members every
    /// module inherits (<c>CapabilityName</c>, <c>ChainType</c>) which every provider legitimately has.
    /// </summary>
    private static IReadOnlyCollection<string> CapabilityMethodNames(Type moduleInterface)
    {
        var inherited = typeof(IBlockchainProviderModule)
            .GetMethods()
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        return moduleInterface
            .GetMethods()
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Where(n => !inherited.Contains(n))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// A minimal provider used only to certify the shared seams every provider inherits: the
/// non-retrying broadcast wrapper and the amount-narrowing gate. Both are protected on
/// <see cref="BaseBlockchainProvider"/>, so the only honest way to test them is from a
/// subclass that adds no behaviour of its own.
/// </summary>
internal sealed class ConformanceProbeProvider : BaseBlockchainProvider
{
    private ConformanceProbeProvider(IConfiguration config)
        : base(config, NullLogger<ConformanceProbeProvider>.Instance)
    {
    }

    public static ConformanceProbeProvider Create() =>
        new(new ConfigurationBuilder().Build());

    public override string ChainType => "ConformanceProbe";

    public Task<T> RunBroadcastAsync<T>(Func<Task<T>> operation) =>
        ExecuteWithRetryAsync(operation, maxRetries: 3, initialDelayMs: 1, safety: RetrySafety.Broadcast);

    public Task<T> RunIdempotentAsync<T>(Func<Task<T>> operation) =>
        ExecuteWithRetryAsync(operation, maxRetries: 3, initialDelayMs: 1, safety: RetrySafety.Idempotent);

    public AZOAResult<string>? Narrow(RailAmount amount, out ulong baseUnits) =>
        NarrowToChainBaseUnits<string>(amount, "conformance probe", out baseUnits);
}
