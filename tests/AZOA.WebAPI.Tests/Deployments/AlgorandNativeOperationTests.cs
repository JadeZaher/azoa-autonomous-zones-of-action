using System.Net;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed class AlgorandNativeOperationTests
{
    [Theory]
    [InlineData("9007199254740993", true)]
    [InlineData("18446744073709551615", true)]
    [InlineData("18446744073709551616", false)]
    [InlineData("01", false)]
    [InlineData("+1", false)]
    [InlineData("0", false)]
    public async Task ForeignIdentifiersRemainLosslessCanonicalStrings(string id, bool valid)
    {
        using var fixture = new Fixture();
        var input = fixture.Intent().Input.Deserialize<AvmNativeInput>()!;
        var application = input.Artifact.Content.Deserialize<AvmApplicationArtifact>()!;
        var artifact = input.Artifact with
        {
            Content = JsonSerializer.SerializeToElement(application with { ForeignApplications = [id], ForeignAssets = [id] }),
        };
        var result = await fixture.Provider.ValidateArtifactAsync(artifact);
        result.IsError.Should().Be(!valid);
        if (valid)
        {
            result.Result!.Content.GetProperty("foreignApplications")[0].GetString().Should().Be(id);
            result.Result.Content.GetProperty("foreignAssets")[0].GetString().Should().Be(id);
        }
        fixture.Rpc.Reads.Should().Be(0);
        fixture.Rpc.Submissions.Should().Be(0);
    }

    [Fact]
    public async Task UploadedBytecodeCannotAssertServerCompilerProvenance()
    {
        using var fixture = new Fixture();
        var artifact = fixture.Intent().Input.Deserialize<AvmNativeInput>()!.Artifact with { CompilerIdentity = "trusted compiler" };
        var result = await fixture.Provider.ValidateArtifactAsync(artifact);
        result.IsError.Should().BeFalse(result.Message);
        using var evidence = JsonDocument.Parse(result.Result!.CompilerIdentity!);
        evidence.RootElement.GetProperty("compilerEvidence").ValueKind.Should().Be(JsonValueKind.Null);
        evidence.RootElement.GetProperty("approval").GetString().Should().Be("uploaded-bytecode");
        var replay = await fixture.Provider.ValidateArtifactAsync(result.Result);
        replay.Result!.CompilerIdentity.Should().Be(result.Result.CompilerIdentity);
        fixture.Rpc.Reads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompilationRequiresStableReportedBuildIdentity(bool changesDuringCompilation)
    {
        using var fixture = new Fixture();
        fixture.Rpc.ChangeCompilerBuild = changesDuringCompilation;
        var artifact = fixture.Intent().Input.Deserialize<AvmNativeInput>()!.Artifact;
        var application = artifact.Content.Deserialize<AvmApplicationArtifact>()!;
        artifact = artifact with { Content = JsonSerializer.SerializeToElement(application with
        {
            Approval = new("Teal", "#pragma version 1\nint 1"),
        }) };
        var result = await fixture.Provider.ValidateArtifactAsync(artifact);
        result.IsError.Should().Be(changesDuringCompilation);
        if (!changesDuringCompilation)
        {
            using var evidence = JsonDocument.Parse(result.Result!.CompilerIdentity!);
            evidence.RootElement.GetProperty("approval").GetString().Should().Be("server-compiled");
            evidence.RootElement.GetProperty("clear").GetString().Should().Be("uploaded-bytecode");
            using var compiler = JsonDocument.Parse(evidence.RootElement.GetProperty("compilerEvidence").GetString()!);
            compiler.RootElement.GetProperty("scheme").GetString().Should().Be("configured-algod-reported/1");
            var reuploaded = await fixture.Provider.ValidateArtifactAsync(result.Result);
            using var uploadEvidence = JsonDocument.Parse(reuploaded.Result!.CompilerIdentity);
            uploadEvidence.RootElement.GetProperty("approval").GetString().Should().Be("uploaded-bytecode");
            uploadEvidence.RootElement.GetProperty("compilerEvidence").ValueKind.Should().Be(JsonValueKind.Null);
        }
        fixture.Rpc.Compilations.Should().Be(1);
        fixture.Rpc.Submissions.Should().Be(0);
    }

    [Fact]
    public async Task ReadOnlyQuoteIncludesDeclaredApplicationMinimumBalance()
    {
        using var fixture = new Fixture();
        var result = await fixture.Provider.QuoteNativeOperationAsync(fixture.Intent());
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.NetworkFee.Should().Be("1000");
        result.Result.LockedRent.Should().Be("178500");
        result.Result.Value.Should().Be("0");
        fixture.Store.VerifyNoOtherCalls();
        fixture.Rpc.Submissions.Should().Be(0);
    }

    [Fact]
    public async Task ChangedFeeRejectsBeforeClaimingPreparationOrEnteringCustody()
    {
        using var fixture = new Fixture();
        var intent = fixture.Intent();
        var quote = (await fixture.Provider.QuoteNativeOperationAsync(intent)).Result!;
        intent = intent with { ProtocolIdentity = quote.ProtocolIdentity };
        quote = quote with { QuoteId = "quote", IntentHash = NativeOperationAuthorization.IntentHash(intent) };
        intent = intent with { Quote = quote, Budget = intent.Budget with { QuoteId = "quote" } };
        fixture.Rpc.MinimumFee = 2000;
        var result = await fixture.Provider.PrepareNativeStageAsync(intent, "Execute", 0);
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("costs");
        fixture.Store.Verify(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        fixture.Store.VerifyNoOtherCalls();
        fixture.Rpc.Submissions.Should().Be(0);
    }

    [Fact]
    public async Task DirectQuoteRejectsBytecodeBeyondDeclaredPages()
    {
        using var fixture = new Fixture();
        var intent = fixture.Intent(approval: Convert.ToBase64String(new byte[2048]));
        var result = await fixture.Provider.QuoteNativeOperationAsync(intent);
        result.IsError.Should().BeTrue();
        fixture.Rpc.Reads.Should().Be(0);
        fixture.Store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PlatformAuthorityCannotSubstituteForDeployingUser()
    {
        using var fixture = new Fixture();
        var intent = fixture.Intent();
        intent = intent with { Authorities = [intent.Authorities[0] with { SigningContext = SigningContext.Platform }] };
        (await fixture.Provider.QuoteNativeOperationAsync(intent)).IsError.Should().BeTrue();
        fixture.Rpc.Reads.Should().Be(0);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        private readonly string _sender = new AlgoAccount().Address.EncodeAsString();
        public readonly Mock<INativePreparationStore> Store = new(MockBehavior.Strict);
        public readonly RpcHandler Rpc = new();
        public readonly AlgorandProvider Provider;

        public Fixture()
        {
            Store.Setup(store => store.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((NativePreparation?)null);
            _services = new ServiceCollection().AddSingleton(Store.Object).BuildServiceProvider();
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "devnet", ["Blockchain:Chains:0:ChainType"] = "Algorand",
                ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true", ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.test/",
                ["Blockchain:Chains:0:Devnet:IndexerUrl"] = "http://indexer.test/",
            }).Build();
            Provider = new AlgorandProvider(config, NullLogger<AlgorandProvider>.Instance,
                signerFactory: null, keyService: null, custodyService: null,
                custodyScopeFactory: _services.GetRequiredService<IServiceScopeFactory>(), httpMessageHandler: Rpc);
        }

        public NativeOperationIntent Intent(string approval = "ASABASI=")
        {
            var artifact = new NativeArtifact("avm-application/1", JsonSerializer.SerializeToElement(new AvmApplicationArtifact(
                new("Bytecode", approval), new("Bytecode", "ASABASI="), new(1, 1), new(0, 0), 0, [], [], [], [], [])), "algod");
            return new(1, Guid.NewGuid().ToString("N"), "Deploy", "Algorand", "Devnet", "", "artifact",
                JsonSerializer.SerializeToElement(new AvmNativeInput(artifact, _sender)),
                [new(_sender, new SigningContext(Guid.NewGuid(), Guid.NewGuid(), false, Scope: AzoaScopes.ContractDeploySign))],
                new("1000", "178500", "0", ""));
        }

        public void Dispose() { _services.Dispose(); Rpc.Dispose(); }
    }

    private sealed class RpcHandler : HttpMessageHandler
    {
        public int MinimumFee = 1000;
        public int Reads;
        public int Submissions;
        public int Compilations;
        public bool ChangeCompilerBuild;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/teal/compile")
            {
                Compilations++;
                return Reply(new { result = "ASABASI=" });
            }
            if (request.Method == HttpMethod.Post) { Submissions++; throw new InvalidOperationException("Unexpected transaction submission."); }
            Reads++;
            if (request.RequestUri!.AbsolutePath == "/versions")
                return Reply(new
                {
                    build = new { commit_hash = ChangeCompilerBuild && Compilations > 0 ? "changed" : "original", major = 4, minor = 0, build_number = 1 },
                    genesis_hash_b64 = Convert.ToBase64String(new byte[32]), genesis_id = "local-v1",
                });
            request.RequestUri!.AbsolutePath.Should().Be("/v2/transactions/params");
            var body = new Dictionary<string, object>
            {
                ["fee"] = 0, ["min-fee"] = MinimumFee, ["last-round"] = 100, ["genesis-id"] = "local-v1",
                ["genesis-hash"] = Convert.ToBase64String(new byte[32]), ["consensus-version"] = "test",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }

        private static Task<HttpResponseMessage> Reply<T>(T value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value)),
        });
    }
}
