using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Services.Wormhole;
using AZOA.WebAPI.Tests.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SurrealForge.Client.Query;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttLifecycleFlowTests
{
    private static WttOperationView Ok(AZOAResult<WttOperationView> result)
    {
        Assert.False(result.IsError, result.Message);
        return result.Result!;
    }

    /// <summary>Real lifecycle, recovery, proof verification and authority binding; chains and guardian HTTP are the faked process edge.</summary>
    private sealed class Rig
    {
        internal readonly Guid Actor = Guid.NewGuid();
        internal readonly FlowClock Clock = new(DateTimeOffset.UtcNow);
        internal readonly InMemoryWttOperationStore Store;
        internal readonly InMemoryWttBodyClaimStore Claims = new();
        internal readonly FakePreparationStore Preparations = new();
        internal readonly GuardianEndpoint Guardians;
        internal readonly FakeWttChain Source;
        internal readonly FakeWttChain Target;
        internal readonly SigningCredentialWitness Credential;
        internal readonly WttLifecycleService Service;
        internal readonly WttRecoveryService Recovery;
        internal readonly Guid SourceWallet = Guid.NewGuid();
        internal readonly Guid TargetWallet = Guid.NewGuid();
        internal string Recipient { get; }
        internal string SourceChain { get; }
        internal const string TargetPayer = "TargetPayer11111111111111111111111111111111";
        internal const string OwnCustodial = "OwnCustodial1111111111111111111111111111111";
        internal const string ClaimedPlatform = "ClaimedPlatform11111111111111111111111111111";

        internal Rig(bool participantRecipient = false, bool unwrapNative = false, string sourceChain = "Algorand")
        {
            Store = new(Clock);
            SourceChain = sourceChain;
            Source = sourceChain == "Ethereum"
                ? new FakeWttChain("Ethereum", 2, "eip155:fake", "e3", Preparations) { NonceAllocating = true }
                : new FakeWttChain("Algorand", 8, "algorand:fake-genesis", "a1", Preparations);
            Target = new FakeWttChain("Solana", 1, "solana:fake-genesis", "b2", Preparations) { ParticipantOptIn = participantRecipient };
            Target.Guardian = GuardianAddress;
            Target.RegisteredEmitter = (Source.ProtocolChain, Source.Emitter);
            Recipient = participantRecipient || unwrapNative ? "ExternalRecipient1111111111111111111111111111" : TargetPayer;
            Guardians = new GuardianEndpoint(Source);
            Credential = new(1, "Jwt", Actor, null, null, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1),
                null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin);
            var wallets = new Mock<IWalletStore>();
            var source = Wallet(SourceWallet, sourceChain, sourceChain == "Ethereum" ? "0x" + new string('5', 40) : "SOURCEPAYERALGORAND", true);
            var target = Wallet(TargetWallet, "Solana", TargetPayer, true);
            // A second key-custodied wallet, and a Platform-typed row registered before custody proof was required.
            var owned = new[] { source, target, Wallet(Guid.NewGuid(), "Solana", OwnCustodial, true), Wallet(Guid.NewGuid(), "Solana", ClaimedPlatform, false) };
            foreach (var wallet in owned)
                wallets.Setup(store => store.GetByIdAsync(wallet.Id, It.IsAny<CancellationToken>())).ReturnsAsync(AZOAResult<IWallet>.Success(wallet));
            wallets.Setup(store => store.GetByUserAsync(Actor, It.IsAny<CancellationToken>()))
                .ReturnsAsync(AZOAResult<IEnumerable<IWallet>>.Success(owned));
            // Tenant credentials resolve one explicit live grant covering the WTT payer scopes.
            var grants = new Mock<ISurrealExecutor>(MockBehavior.Strict);
            grants.Setup(executor => executor.QueryAsync<ConsentGrant>(It.IsAny<SurrealQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new ConsentGrant { Id = Guid.NewGuid().ToString("N"), Scopes = "wtt:source:sign,wtt:target:sign",
                    Origin = "UserExplicit", GrantedAt = DateTimeOffset.UtcNow.AddMinutes(-1) } });
            var authorities = new SigningAdmissionAuthorityFactory(new Mock<IUserStore>(MockBehavior.Strict).Object, wallets.Object, grants.Object);
            var keys = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["AZOA:WalletEncryptionKey"] = "wtt-flow-fixture-encryption-key" }).Build();
            var custody = new KeyCustodyService(wallets.Object, new WalletKeyService(keys), keys, new Mock<ITenantConsentGate>().Object);
            var modules = new WttModuleResolver(Providers(Source, Target));
            var signatures = new Mock<IWttGuardianSignatureVerifier>();
            signatures.Setup(value => value.VerifyGuardianSignature(It.IsAny<byte[]>(), It.IsAny<WormholeVaaSignature>(), It.IsAny<byte[]>()))
                .Returns((byte[] digest, WormholeVaaSignature signature, byte[] expected)
                    => signature.R.SequenceEqual(digest) && Convert.ToHexStringLower(expected) == GuardianAddress);
            var verifier = new WttProofVerifier(signatures.Object, TimeProvider.System,
                Options.Create(new WttFinalityOptions { Networks = [new(Target.ProtocolIdentity, 90)] }));
            var transport = new WttGuardianTransport(new HttpClient(Guardians), Options.Create(new WttGuardianOptions
                { Endpoints = ["https://guardian.test/"], TimeoutSeconds = 5 }));
            Recovery = new WttRecoveryService(Store, modules, transport, verifier, Claims, NullLogger<WttRecoveryService>.Instance);
            Service = new WttLifecycleService(Store, wallets.Object, authorities, modules, Preparations, Recovery, custody, Clock);
            UnwrapNative = unwrapNative;
        }

        private AZOA.WebAPI.Models.Wallet Wallet(Guid id, string chain, string address, bool custodied) => new()
        {
            Id = id, UserId = Actor, ChainType = chain, Address = address, WalletType = WalletType.Platform,
            EncryptedPrivateKey = custodied ? "custody-ciphertext" : null,
        };

        internal bool UnwrapNative { get; }
        internal const string GuardianAddress = "5893b5a76c3f739645648885bdccc06cd70a3cd3";

        internal WttQuoteRequest Request(string key) => new(key, new(SourceChain, "Devnet", SourceWallet), "31566704", false,
            "1000000", null, new("Solana", "Devnet", TargetWallet), Recipient, UnwrapNative,
            new("1000", "100000", "1000"), new("1000", "5000000", "1000"));

        internal async Task<WttOfferView> QuoteAsync(string key)
        {
            var quoted = await Service.QuoteAsync(Credential, Request(key));
            Assert.False(quoted.IsError, quoted.Message);
            return quoted.Result!;
        }

        internal async Task<WttOperationView> StartAsync(string key)
        {
            var offer = await QuoteAsync(key);
            return Ok(await Service.StartAsync(Credential, new(offer.OfferId, offer.OfferHash, true), key));
        }

        /// <summary>Drives a started transfer to target authorization: source send, finality, guardian proof.</summary>
        internal async Task<WttOperationView> PublishAndProveAsync(WttOperationView view)
        {
            view = Ok(await Service.ContinueAsync(view.OperationId, new(view.Version, true), Credential));
            Source.Finalize(view.Stages.Last().TransactionId!);
            view = Ok(await Service.RefreshAsync(view.OperationId, Credential));
            Assert.Equal("AwaitingTargetAuthorization", view.PublicState);
            return view;
        }

        private static IBlockchainProviderFactory Providers(params FakeWttChain[] chains)
        {
            var factory = new Mock<IBlockchainProviderFactory>();
            foreach (var chain in chains)
            {
                var rail = new Mock<IBlockchainProvider>();
                rail.SetupGet(value => value.ActiveNetwork).Returns(ChainNetwork.Devnet);
                rail.SetupGet(value => value.ChainType).Returns(chain.ChainType);
                IValueRailProvider? provider = rail.Object;
                factory.Setup(value => value.TryGetRailProvider(chain.ChainType, It.IsAny<RailEnvironment>(), out provider)).Returns(true);
                IWttSourceModule? source = chain; IWttTargetModule? target = chain; IWttGuardianStateSource? guardian = chain;
                IWttParticipantModule? participant = chain; IWttCleanupModule? cleanup = chain; IWttNativeOutputModule? native = chain;
                factory.Setup(value => value.TryGetModule(rail.Object, out source)).Returns(true);
                factory.Setup(value => value.TryGetModule(rail.Object, out target)).Returns(true);
                factory.Setup(value => value.TryGetModule(rail.Object, out guardian)).Returns(true);
                factory.Setup(value => value.TryGetModule(rail.Object, out participant)).Returns(true);
                factory.Setup(value => value.TryGetModule(rail.Object, out cleanup)).Returns(true);
                factory.Setup(value => value.TryGetModule(rail.Object, out native)).Returns(true);
                if (!chain.NonceAllocating) continue;
                IWttNonceReservationModule? nonces = chain;
                factory.Setup(value => value.TryGetModule(rail.Object, out nonces)).Returns(true);
            }
            return factory.Object;
        }
    }

    /// <summary>Test clock shared by the lifecycle service and journal so time rules are stepped, not slept.</summary>
    internal sealed class FlowClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    /// <summary>Guardian HTTP edge: 404 while pending, then one exact signed envelope for the published body.</summary>
    private sealed class GuardianEndpoint(FakeWttChain source) : HttpMessageHandler
    {
        internal bool Pending { get; set; }
        internal bool WrongSigner { get; set; }
        internal byte SignatureFill { get; set; } = 1;
        internal int Requests { get; private set; }
        internal WttVaa? Vaa { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var publication = source.LastPublication;
            if (Pending || publication is null || !request.RequestUri!.AbsolutePath.EndsWith(
                    $"/v1/signed_vaa/{publication.EmitterChain}/{publication.EmitterAddress}/{publication.Sequence}", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = Convert.FromHexString(publication.BodyHex);
            var envelope = new byte[6 + 66 + body.Length];
            envelope[0] = 1; envelope[5] = 1;
            if (WrongSigner) envelope.AsSpan(7, 32).Fill(0x5a);
            else Convert.FromHexString(publication.BodyDigest).CopyTo(envelope, 7);
            envelope.AsSpan(39, 32).Fill(SignatureFill);
            body.CopyTo(envelope, 72);
            Vaa = WttVaaCodec.Parse(envelope);
            var json = JsonSerializer.Serialize(new { vaaBytes = Convert.ToBase64String(envelope) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Provider-shaped preparation journal: one claim per generation and seal-before-sign/send refusal.</summary>
    internal sealed class FakePreparationStore : IWttPreparationStore
    {
        private readonly Dictionary<string, WttPreparation> _rows = new(StringComparer.Ordinal);
        internal IReadOnlyCollection<string> Sealed => _rows.Values.Where(row => row.Sealed).Select(row => row.Id).ToArray();

        public Task<WttPreparation?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_rows.TryGetValue(id, out var row) ? row : null);

        public Task<WttPreparation> ClaimAsync(WttPreparation candidate, CancellationToken ct = default)
        {
            if (!_rows.TryAdd(candidate.Id, candidate) && _rows[candidate.Id].BindingJson != candidate.BindingJson)
                throw new InvalidOperationException("WTT stage belongs to another immutable quote or intent.");
            return Task.FromResult(_rows[candidate.Id]);
        }

        public Task<WttPreparation?> TrySaveSignedAsync(WttPreparation expected, string signedPreparationJson, CancellationToken ct = default)
        {
            var row = _rows[expected.Id];
            if (row.Sealed || row.SignedPreparationJson is not null) return Task.FromResult<WttPreparation?>(null);
            row.SignedPreparationJson = signedPreparationJson; row.Version++;
            return Task.FromResult<WttPreparation?>(row);
        }

        public Task<WttPreparation?> SealAsync(string id, string bindingJson, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(id, out var row) || row.BindingJson != bindingJson) return Task.FromResult<WttPreparation?>(null);
            row.Sealed = true;
            return Task.FromResult<WttPreparation?>(row);
        }
    }

    /// <summary>Chain adapter edge with real canonical WTT bodies; signing, sending and finality are recorded, never assumed.</summary>
    internal sealed class FakeWttChain(string chainType, ushort protocolChain, string protocolIdentity, string seed, FakePreparationStore preparations)
        : IWttSourceModule, IWttTargetModule, IWttGuardianStateSource, IWttParticipantModule, IWttCleanupModule, IWttNativeOutputModule,
            IWttNonceReservationModule
    {
        private readonly HashSet<string> _finalized = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _nonces = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
        private ulong _sequence = 41;
        public string CapabilityName => "Fake.Wtt";
        public string ChainType => chainType;
        internal ushort ProtocolChain => protocolChain;
        internal string ProtocolIdentity => protocolIdentity;
        internal string Emitter => new string(seed[0], 63) + seed[1];
        internal string Guardian { get; set; } = "";
        internal (ushort Chain, string Address) RegisteredEmitter { get; set; }
        internal bool ParticipantOptIn { get; init; }
        internal bool FailNextPrepare { get; set; }
        internal bool NonceAllocating { get; init; }
        internal bool CrashAfterReserve { get; set; }
        internal string? FailAfter { get; set; }
        internal Func<Task>? OnNextProbe { get; set; }
        internal int Reservations { get; private set; }
        internal bool LoseNextSend { get; set; }
        internal string NextFee { get; set; } = "100";
        internal int Custody { get; private set; }
        internal int Quotes { get; private set; }
        internal List<string> Sent { get; } = [];
        internal WttPublication? LastPublication { get; private set; }

        internal WttNetworkDescriptor Network => new(chainType, ChainNetwork.Devnet, protocolIdentity, "Devnet", protocolChain,
            "core-" + seed, "token-" + seed, new string('c', 64),
            new(protocolChain, new string(seed[1], 64), new string('3', 63) + seed[1], new string('4', 63) + seed[1]), Emitter);

        internal void Finalize(string transactionId) => _finalized.Add(transactionId);

        internal WttNonceHold Hold(string phase, string stage, int generation)
            => _nonces.GetValueOrDefault(phase + "|" + stage + "|" + generation) switch
            {
                null or "Released" => WttNonceHold.None,
                "Reserved" => WttNonceHold.Unsigned,
                _ => WttNonceHold.Held,
            };

        public async Task<WttNonceHold> ReadWttNonceHoldAsync(WttOperationIntent intent, string phase, string stage, int generation,
            CancellationToken ct = default)
        {
            if (OnNextProbe is { } probe) { OnNextProbe = null; await probe(); }
            return Hold(phase, stage, generation);
        }

        public Task<bool> ReleaseUnsignedWttReservationAsync(WttOperationIntent intent, string phase, string stage, int generation,
            CancellationToken ct = default)
        {
            var key = phase + "|" + stage + "|" + generation;
            if (_nonces.GetValueOrDefault(key) != "Reserved") return Task.FromResult(false);
            _nonces[key] = "Released";
            return Task.FromResult(true);
        }
        internal void FailOnChain(string transactionId) => _failed.Add(transactionId);

        public Task<AZOAResult<(WttNetworkDescriptor Network, WttAssetDescriptor Asset)>> InspectWttSourceAsync(string tokenAddress,
            bool nativeAsset, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<(WttNetworkDescriptor, WttAssetDescriptor)>.Success((Network,
                new WttAssetDescriptor(tokenAddress, nativeAsset, 6, protocolChain, new string('9', 64), 6, new string('0', 64),
                    new string('0', 64), "fixture-asa"))));

        public Task<AZOAResult<WttPublicationRules>> ReadWttPublicationRulesAsync(CancellationToken ct = default)
            => Task.FromResult(AZOAResult<WttPublicationRules>.Success(new(32, false)));

        public Task<AZOAResult<WttStageQuote>> QuoteWttSourceAsync(WttOperationIntent intent, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<WttStageQuote>.Success(Quote(intent, "Source",
                [new("Transfer", NextFee, "0", "0", intent.SourceAuthority.Address), new("Cleanup:Transfer", "10", "0", "0", intent.SourceAuthority.Address)])));

        public Task<AZOAResult<WttStagePreparation>> PrepareWttSourceAsync(WttOperationIntent intent, WttStageQuote quote, string stage,
            int generation, CancellationToken ct = default)
            => Task.FromResult(Prepare(intent, quote, "Source", stage, generation));

        public Task SubmitWttSourceAsync(WttOperationIntent intent, WttStageQuote quote, WttStagePreparation prepared, CancellationToken ct = default)
            => Send(intent, quote, "Source", prepared);

        public Task<AZOAResult<WttStageObservation>> ObserveWttSourceAsync(WttOperationIntent intent, WttStageQuote quote,
            WttStagePreparation prepared, CancellationToken ct = default)
        {
            if (_failed.Contains(prepared.TransactionId))
                return Task.FromResult(AZOAResult<WttStageObservation>.Success(new("FailedOnChain", prepared.TransactionId, protocolIdentity,
                    "{}", NetworkFeePaid: "7")));
            if (!_finalized.Contains(prepared.TransactionId))
                return Task.FromResult(AZOAResult<WttStageObservation>.Success(new("Pending", prepared.TransactionId, protocolIdentity, Detail: "Not final.")));
            LastPublication ??= Publication(intent, prepared.TransactionId);
            return Task.FromResult(AZOAResult<WttStageObservation>.Success(new("Confirmed", prepared.TransactionId, protocolIdentity,
                "{}", LastPublication, "7")));
        }

        public Task<AZOAResult<WttTargetInspection>> InspectWttTargetAsync(WttAssetDescriptor origin, string recipientAddress,
            bool unwrapNative, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<WttTargetInspection>.Success(new(Network, "wrapped-" + seed, 6, true, !ParticipantOptIn,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(recipientAddress))))));

        public Task<AZOAResult<WttStageQuote>> QuoteWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, CancellationToken ct = default)
        {
            _ = WttProofBinding.Transfer(intent, proofs.Transfer);
            IReadOnlyList<WttStageReservation> stages = ParticipantOptIn
                ? [new("RecipientOptIn", "0", "2039280", "0", intent.RecipientAddress), new("Redeem", NextFee, "0", "0", intent.TargetAuthority.Address)]
                : [new("Redeem", NextFee, "0", "0", intent.TargetAuthority.Address), new("Cleanup:Redeem", "10", "0", "0", intent.TargetAuthority.Address)];
            return Task.FromResult(AZOAResult<WttStageQuote>.Success(Quote(intent, "Target", stages)));
        }

        public Task<AZOAResult<WttStagePreparation>> PrepareWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote,
            string stage, int generation, CancellationToken ct = default)
            => Task.FromResult(Prepare(intent, quote, "Target", stage, generation));

        public Task SubmitWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote, WttStagePreparation prepared,
            CancellationToken ct = default)
            => Send(intent, quote, "Target", prepared);

        public Task<AZOAResult<WttStageObservation>> ObserveWttTargetAsync(WttOperationIntent intent, WttTargetProofs proofs, WttStageQuote quote,
            WttStagePreparation prepared, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<WttStageObservation>.Success(_finalized.Contains(prepared.TransactionId)
                ? new("Confirmed", prepared.TransactionId, protocolIdentity, JsonSerializer.Serialize(new { redeemed = intent.Amount.RecipientAmount }),
                    NetworkFeePaid: "5")
                : new("Pending", prepared.TransactionId, protocolIdentity, Detail: "Not final.")));

        public Task<AZOAResult<WttGuardianEvidence>> ReadWttGuardianEvidenceAsync(uint guardianSetIndex, ushort emitterChain, string emitterAddress,
            CancellationToken ct = default)
            => Task.FromResult(AZOAResult<WttGuardianEvidence>.Success(new(protocolIdentity, Network.CoreBridge, Network.TokenBridge, 0, 0,
                [Guardian], 0, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "finalized-block", DateTimeOffset.UtcNow,
                RegisteredEmitter.Chain, RegisteredEmitter.Address)));

        public Task<AZOAResult<WttParticipantPreparation>> PrepareWttParticipantAsync(WttOperationIntent intent, WttStageQuote quote,
            string stage, int generation, CancellationToken ct = default)
        {
            var reservation = quote.Stages.Single(value => value.Stage == stage);
            return Task.FromResult(AZOAResult<WttParticipantPreparation>.Success(new(intent.OperationId, stage, generation,
                intent.RecipientAddress, protocolIdentity, "unsigned-" + stage + "-" + generation, "{}", reservation)));
        }

        public Task<AZOAResult<WttStagePreparation>> AcceptWttParticipantSignatureAsync(WttOperationIntent intent, WttStageQuote quote,
            WttParticipantPreparation prepared, string signedTransactionBase64, CancellationToken ct = default)
            => Task.FromResult(signedTransactionBase64 == "signed:" + prepared.UnsignedTransactionBase64
                ? AZOAResult<WttStagePreparation>.Success(new(prepared.Stage, prepared.Generation, "participant-" + prepared.Stage,
                    signedTransactionBase64, protocolIdentity, prepared.RecoveryMetadata, prepared.Reservation))
                : AZOAResult<WttStagePreparation>.Failure("Signature does not cover the exact disclosed message."));

        public Task<AZOAResult<WttCleanupQuote>> QuoteWttCleanupAsync(WttOperationIntent intent, WttStageQuote originalQuote,
            WttTargetProofs? proofs, WttStagePreparation original, CancellationToken ct = default)
        {
            var owner = originalQuote.Phase == "Source" ? intent.SourceAuthority : intent.TargetAuthority;
            var quote = new WttCleanupQuote(1, "", CanonicalJson.Hash(intent), originalQuote.QuoteId, originalQuote.Phase, original.Stage,
                original.Generation, CanonicalJson.Hash(original), protocolIdentity, "stage-account-" + seed, owner.Address, "500", "400", "5",
                "observation", "{}", DateTimeOffset.UtcNow.AddSeconds(30));
            return Task.FromResult(AZOAResult<WttCleanupQuote>.Success(quote with { QuoteId = CanonicalJson.Hash(quote) }));
        }

        public Task<AZOAResult<WttStagePreparation>> PrepareWttCleanupAsync(WttOperationIntent intent, WttStageQuote originalQuote,
            WttTargetProofs? proofs, WttStagePreparation original, WttCleanupQuote quote, WttCleanupConsent consent, WttSigningAuthority authority,
            int generation, CancellationToken ct = default)
        {
            if (authority.Context.Scope != AzoaScopes.WttCleanupSign || authority.Context.ActingTenantId != Guid.Empty
                || authority.Address != quote.DestinationAddress)
                return Task.FromResult(AZOAResult<WttStagePreparation>.Failure("Cleanup requires fresh direct original-owner consent."));
            Custody++;
            var stage = quote.Phase + ":" + quote.OriginalStage + ":" + quote.OriginalGeneration + ":" + CanonicalJson.Hash(new { consent.RequestKey });
            return Task.FromResult(AZOAResult<WttStagePreparation>.Success(new(stage, generation, "cleanup-" + generation,
                "cleanup-wire-" + generation, protocolIdentity, "{}", new(stage, quote.NetworkFee, "0", "0", quote.DestinationAddress))));
        }

        public Task SubmitWttCleanupAsync(WttOperationIntent intent, WttCleanupQuote quote, WttCleanupConsent consent, WttStagePreparation prepared,
            CancellationToken ct = default)
        {
            Sent.Add(prepared.SignedTransactionBase64);
            return Task.CompletedTask;
        }

        public Task<AZOAResult<WttCleanupObservation>> ObserveWttCleanupAsync(WttOperationIntent intent, WttCleanupQuote quote,
            WttCleanupConsent consent, WttStagePreparation prepared, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<WttCleanupObservation>.Success(_finalized.Contains(prepared.TransactionId)
                ? new(new("Confirmed", prepared.TransactionId, protocolIdentity, "{}", NetworkFeePaid: "5"), "500", "400")
                : new(new("Pending", prepared.TransactionId, protocolIdentity), null, null)));

        public Task<AZOAResult<WttNativeOutputQuote>> QuoteWttNativeOutputAsync(WttOperationIntent intent, WttTargetProofs proofs,
            WttStageQuote targetQuote, WttStagePreparation redemption, CancellationToken ct = default)
        {
            var quote = new WttNativeOutputQuote(1, "", CanonicalJson.Hash(intent), CanonicalJson.Hash(redemption), "observation",
                protocolIdentity, intent.RecipientAddress, "So11111111111111111111111111111111111111112", "recipient-ata", "990", "5000",
                "2039280", "{}", DateTimeOffset.UtcNow.AddSeconds(30));
            return Task.FromResult(AZOAResult<WttNativeOutputQuote>.Success(quote with { QuoteId = CanonicalJson.Hash(quote) }));
        }

        public Task<AZOAResult<WttParticipantPreparation>> PrepareWttNativeOutputAsync(WttOperationIntent intent, WttNativeOutputQuote quote,
            WttNativeOutputConsent consent, int generation, CancellationToken ct = default)
        {
            var stage = "Unwrap:" + CanonicalJson.Hash(new { consent.RequestKey });
            return Task.FromResult(AZOAResult<WttParticipantPreparation>.Success(new(intent.OperationId, stage, generation, quote.RecipientAddress,
                protocolIdentity, "unsigned-native-" + generation, "{}", new(stage, quote.NetworkFee, quote.TemporaryRent, "0", quote.RecipientAddress))));
        }

        public Task<AZOAResult<WttStagePreparation>> AcceptWttNativeOutputSignatureAsync(WttOperationIntent intent, WttNativeOutputQuote quote,
            WttNativeOutputConsent consent, WttParticipantPreparation prepared, string signedTransactionBase64, CancellationToken ct = default)
            => Task.FromResult(signedTransactionBase64 == "signed:" + prepared.UnsignedTransactionBase64
                ? AZOAResult<WttStagePreparation>.Success(new(prepared.Stage, prepared.Generation, "native-" + prepared.Generation,
                    signedTransactionBase64, protocolIdentity, "{}", prepared.Reservation))
                : AZOAResult<WttStagePreparation>.Failure("Signature does not cover the exact disclosed message."));

        public Task SubmitWttNativeOutputAsync(WttOperationIntent intent, WttNativeOutputQuote quote, WttNativeOutputConsent consent,
            WttStagePreparation prepared, CancellationToken ct = default)
        {
            Sent.Add(prepared.SignedTransactionBase64);
            return Task.CompletedTask;
        }

        public Task<AZOAResult<WttNativeOutputObservation>> ObserveWttNativeOutputAsync(WttOperationIntent intent, WttNativeOutputQuote quote,
            WttNativeOutputConsent consent, WttStagePreparation prepared, CancellationToken ct = default)
            => Task.FromResult(AZOAResult<WttNativeOutputObservation>.Success(_finalized.Contains(prepared.TransactionId)
                ? new(new("Confirmed", prepared.TransactionId, protocolIdentity, "{}", NetworkFeePaid: "5000"), quote.NativeAmount, quote.TemporaryRent)
                : new(new("Pending", prepared.TransactionId, protocolIdentity), null, null)));

        private WttStageQuote Quote(WttOperationIntent intent, string phase, IReadOnlyList<WttStageReservation> stages)
        {
            Quotes++;
            var hash = CanonicalJson.Hash(intent);
            var expires = DateTimeOffset.UtcNow.AddSeconds(30);
            var plan = JsonSerializer.Serialize(new { phase, quote = Quotes });
            return new(CanonicalJson.Hash(new { Version = 1, IntentHash = hash, Plan = plan, ExpiresAt = expires }), hash, protocolIdentity,
                phase, stages, plan, expires);
        }

        private AZOAResult<WttStagePreparation> Prepare(WttOperationIntent intent, WttStageQuote quote, string phase, string stage, int generation)
        {
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId, intent.OperationId,
                phase, stage, generation, quote.IntentHash, quote.QuoteId);
            var id = WttPreparationIdentity.Id(binding);
            var row = preparations.GetAsync(id).Result;
            if (row?.Sealed == true) return AZOAResult<WttStagePreparation>.Failure("WTT generation is sealed.");
            if (row?.SignedPreparationJson is { } signed) return AZOAResult<WttStagePreparation>.Success(JsonSerializer.Deserialize<WttStagePreparation>(signed)!);
            row = preparations.ClaimAsync(new WttPreparation { Id = id, BindingJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(binding)),
                PlanJson = "{}" }).Result;
            if (FailNextPrepare)
            {
                FailNextPrepare = false;
                return AZOAResult<WttStagePreparation>.Failure("WTT quote expired before signature creation.");
            }
            if (NonceAllocating)
            {
                // Deterministic per-generation reservation, as the EVM allocator keys it; re-entry reuses it.
                var key = phase + "|" + stage + "|" + generation;
                if (!_nonces.ContainsKey(key)) { _nonces[key] = "Reserved"; Reservations++; }
                if (CrashAfterReserve) { CrashAfterReserve = false; throw new IOException("Process stopped after the nonce reservation committed."); }
                if (FailAfter == "reserve") { FailAfter = null; return AZOAResult<WttStagePreparation>.Failure("Custody refused to sign the reserved nonce."); }
                _nonces[key] = "Signed";
                if (FailAfter == "sign") { FailAfter = null; return AZOAResult<WttStagePreparation>.Failure("The signed candidate failed post-signature validation."); }
            }
            Custody++;
            var reservation = quote.Stages.Single(value => value.Stage == stage);
            var transactionId = chainType + "-" + phase + "-" + stage + "-" + generation;
            var prepared = new WttStagePreparation(stage, generation, transactionId, Convert.ToBase64String(Encoding.UTF8.GetBytes(transactionId)),
                protocolIdentity, "{}", reservation);
            preparations.TrySaveSignedAsync(row, JsonSerializer.Serialize(prepared)).Wait();
            return AZOAResult<WttStagePreparation>.Success(prepared);
        }

        private Task Send(WttOperationIntent intent, WttStageQuote quote, string phase, WttStagePreparation prepared)
        {
            var binding = new WttPreparationBinding(quote.ProtocolIdentity, intent.ActorUserId, intent.TenantId, intent.OperationId,
                phase, prepared.Stage, prepared.Generation, quote.IntentHash, quote.QuoteId);
            if (preparations.GetAsync(WttPreparationIdentity.Id(binding)).Result is { Sealed: true })
                throw new InvalidDataException("Sealed WTT generation cannot be sent.");
            Sent.Add(prepared.SignedTransactionBase64);
            if (LoseNextSend)
            {
                LoseNextSend = false;
                throw new HttpRequestException("Response lost after the node accepted the exact wire.");
            }
            return Task.CompletedTask;
        }

        private WttPublication Publication(WttOperationIntent intent, string transactionId)
        {
            var payload = WttPayloadCodec.EncodeTransfer(new(intent.Amount.Amount, intent.Asset.OriginAddress, intent.Asset.OriginChain,
                intent.RecipientProtocolAddress, intent.Target.ProtocolChain, intent.Amount.Fee));
            var body = new byte[51 + payload.Length];
            var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BinaryPrimitives.WriteUInt32BigEndian(body, timestamp);
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), intent.Nonce);
            BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), protocolChain);
            Convert.FromHexString(Emitter).CopyTo(body, 10);
            var sequence = ++_sequence;
            BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(42), sequence);
            body[50] = intent.ConsistencyLevel;
            payload.CopyTo(body, 51);
            var digest = Convert.ToHexStringLower(Keccak256.ComputeHash(Keccak256.ComputeHash(body)));
            return new(protocolIdentity, Network.CoreBridge, Network.TokenBridge, transactionId, "block", "0", timestamp, intent.Nonce,
                protocolChain, Emitter, sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), intent.ConsistencyLevel,
                Convert.ToHexStringLower(payload), Convert.ToHexStringLower(body), digest);
        }
    }
}
