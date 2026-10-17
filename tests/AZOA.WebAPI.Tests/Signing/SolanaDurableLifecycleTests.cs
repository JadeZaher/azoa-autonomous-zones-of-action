using System.Buffers.Binary;
using System.Reflection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;

namespace AZOA.WebAPI.Tests.Signing;

public sealed class SolanaDurableLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_durable_signer_preserves_exact_legacy_or_version_zero_message(bool versionZero)
    {
        using var fixture = new SolanaDurableFixture();
        var message = new TransactionBuilder().SetRecentBlockHash(SolanaDurableFixture.Blockhash)
            .SetFeePayer(fixture.Actor.PublicKey)
            .AddInstruction(SystemProgram.Transfer(fixture.Actor.PublicKey, fixture.Recipient.PublicKey, 1)).CompileMessage();
        if (versionZero)
        {
            var bytes = new byte[88]; bytes[0] = 1;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), ulong.MaxValue);
            fixture.Recipient.PublicKey.KeyBytes.CopyTo(bytes, 56);
            var table = new SolanaLookupTableSnapshot(fixture.Treasury.PublicKey.Key, SolanaLookupTableSnapshot.Program,
                Convert.ToBase64String(bytes), 10);
            message = SolanaMessageEnvelope.CompileVersionZero(message, [table], out _);
        }
        var method = fixture.Provider.GetType().GetMethod("SignDurableAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var contexts = new Dictionary<string, SigningContext>
        {
            [fixture.Actor.PublicKey.Key] = SigningContext.ForUser(fixture.ActorId, fixture.ActorWalletId).ForChain("Solana")
        };
        var result = await (Task<AZOAResult<byte[]>>)method.Invoke(fixture.Provider, [message, contexts, CancellationToken.None])!;
        result.IsError.Should().BeFalse(result.Message);
        SolanaMessageEnvelope.VerifyWire(result.Result!, message).Should().NotBeNullOrWhiteSpace();
        SolanaMessageEnvelope.Read(message).VersionZero.Should().Be(versionZero);
        fixture.Consents.Should().ContainSingle(context => context.UserId == fixture.ActorId && context.WalletId == fixture.ActorWalletId);
        fixture.Sends.Should().Be(0);
    }

    private const string MetadataProgram = "metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s";

    [Fact]
    public async Task OversizedDurablePacketRefusesBeforeAnyCustodyCallback()
    {
        using var fixture = new SolanaDurableFixture();
        var message = new TransactionBuilder().SetRecentBlockHash(SolanaDurableFixture.Blockhash)
            .SetFeePayer(fixture.Actor.PublicKey)
            .AddInstruction(MemoProgram.NewMemoV2(new string('x', 1200), fixture.Actor.PublicKey)).CompileMessage();
        var method = fixture.Provider.GetType().GetMethod("SignDurableAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var contexts = new Dictionary<string, SigningContext>
        {
            [fixture.Actor.PublicKey.Key] = SigningContext.ForUser(fixture.ActorId, fixture.ActorWalletId).ForChain("Solana"),
        };
        var result = await (Task<AZOAResult<byte[]>>)method.Invoke(fixture.Provider, [message, contexts, CancellationToken.None])!;
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("packet limit");
        fixture.Consents.Should().BeEmpty();
        fixture.Sends.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformAddressCollision_CannotReplaceUserCustodyConsent(bool acceptance)
    {
        using var fixture = new SolanaDurableFixture();
        var intent = fixture.Mint(credential: true);
        var grant = intent.Authorization!;
        intent = acceptance
            ? intent with { Kind = "Accept", AssetId = fixture.Treasury.PublicKey.Key,
                RecipientAddress = fixture.Platform.PublicKey.Key, SignerAddress = fixture.Platform.PublicKey.Key,
                SigningContext = AZOA.WebAPI.Core.SigningContext.ForUser(fixture.RecipientId, fixture.RecipientWalletId),
                PreparationKey = grant.LifecycleId + ":Accept:0", Authorization = grant with { Stage = "Accept" } }
            : intent with { Authorization = grant with { ActorAddress = fixture.Platform.PublicKey.Key, IssuerAddress = fixture.Platform.PublicKey.Key } };
        var result = await fixture.Provider.PrepareNftTransactionAsync(intent);
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("custody consent");
        fixture.Consents.Should().BeEmpty(); fixture.Sends.Should().Be(0);
    }

    [Fact]
    public async Task NormalMint_PreparesDeterministicEditionAndAtomicFeeWithoutBroadcast()
    {
        using var fixture = new SolanaDurableFixture();
        var intent = await fixture.QuotedMintAsync(fee: "17");
        var first = await fixture.Provider.PrepareNftTransactionAsync(intent);
        var second = await fixture.Provider.PrepareNftTransactionAsync(intent);
        first.IsError.Should().BeFalse(first.Message); second.IsError.Should().BeFalse(second.Message);
        first.Result!.TransactionId.Should().Be(second.Result!.TransactionId);
        first.Result.SignedTransactionBase64.Should().Be(second.Result.SignedTransactionBase64);
        first.Result.TokenStandard.Should().Be("MetaplexNonFungible");
        fixture.Sends.Should().Be(0);
        var transaction = Transaction.Deserialize(Convert.FromBase64String(first.Result.SignedTransactionBase64));
        transaction.VerifySignatures().Should().BeTrue();
        var message = Message.Deserialize(transaction.CompileMessage());
        var metadata = message.Instructions.Where(item => message.AccountKeys[item.ProgramIdIndex].Key == MetadataProgram).ToArray();
        metadata.Select(item => item.Data[0]).Should().Equal(33, 17);
        metadata[1].Data.Should().Equal(new byte[] { 17, 1, 0, 0, 0, 0, 0, 0, 0, 0 });
        var payments = message.Instructions.Where(item => message.AccountKeys[item.ProgramIdIndex].Key == SystemProgram.ProgramIdKey.Key
            && BinaryPrimitives.ReadUInt32LittleEndian(item.Data) == 2).ToArray();
        payments.Should().ContainSingle();
        BinaryPrimitives.ReadUInt64LittleEndian(payments[0].Data.AsSpan(4)).Should().Be(17);
        message.AccountKeys[payments[0].KeyIndices[1]].Key.Should().Be(fixture.Treasury.PublicKey.Key);
    }

    [Fact]
    public async Task CredentialMint_RequiresIssuerAndPlatform_RemovesMintAuthority_UsesTruthfulStandard()
    {
        using var fixture = new SolanaDurableFixture();
        var result = await fixture.Provider.PrepareNftTransactionAsync(await fixture.QuotedMintAsync(credential: true));
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.TokenStandard.Should().Be("SPLFrozenCredential");
        var transaction = Transaction.Deserialize(Convert.FromBase64String(result.Result.SignedTransactionBase64));
        var message = Message.Deserialize(transaction.CompileMessage());
        message.Header.RequiredSignatures.Should().Be(2);
        message.AccountKeys.Take(2).Select(key => key.Key).Should().BeEquivalentTo(fixture.Actor.PublicKey.Key, fixture.Platform.PublicKey.Key);
        transaction.VerifySignatures().Should().BeTrue();
        var token = message.Instructions.Where(item => message.AccountKeys[item.ProgramIdIndex].Key == TokenProgram.ProgramIdKey.Key).ToArray();
        var removeAuthority = token.Single(item => item.Data[0] == 6);
        removeAuthority.Data[1].Should().Be(0);
        removeAuthority.Data[2].Should().Be(0);
        message.Instructions.Where(item => message.AccountKeys[item.ProgramIdIndex].Key == MetadataProgram).Should().ContainSingle();
        fixture.Consents.Should().Contain(context => context.UserId == fixture.ActorId && !context.IsPlatform);
        fixture.Consents.Should().Contain(context => context.IsPlatform && context.ChainType == "Solana");
    }

    [Fact]
    public async Task PreparedNft_RejectsChangedFeeIntentOrAppendedSignedBytes()
    {
        using var fixture = new SolanaDurableFixture();
        var result = await fixture.Provider.PrepareNftTransactionAsync(await fixture.QuotedMintAsync(fee: "17"));
        result.IsError.Should().BeFalse(result.Message);
        var prepared = result.Result!;
        Func<Task> altered = () => fixture.Provider.SubmitNftTransactionAsync(prepared with { Intent = prepared.Intent with { RecipientAddress = fixture.Recipient.PublicKey.Key } });
        await altered.Should().ThrowAsync<InvalidOperationException>();
        var junk = Convert.FromBase64String(prepared.SignedTransactionBase64).Concat(new byte[] { 0 }).ToArray();
        Func<Task> appended = () => fixture.Provider.SubmitNftTransactionAsync(prepared with { SignedTransactionBase64 = Convert.ToBase64String(junk) });
        await appended.Should().ThrowAsync<InvalidOperationException>();
        fixture.Sends.Should().Be(0);
    }

    [Fact]
    public async Task ExpiredUnknownHistory_RemainsOperatorRecovery_WithoutInventingNoEffect()
    {
        using var fixture = new SolanaDurableFixture();
        var prepared = (await fixture.Provider.PrepareNftTransactionAsync(await fixture.QuotedMintAsync())).Result!;
        fixture.Height = 151;
        var result = await fixture.Provider.ObserveNftTransactionAsync(prepared);
        result.Result!.Status.Should().Be("NeedsOperator");
        Func<Task> expiredSend = () => fixture.Provider.SubmitNftTransactionAsync(prepared);
        await expiredSend.Should().ThrowAsync<InvalidOperationException>();
        fixture.Sends.Should().Be(0);
    }

    [Theory]
    [InlineData(false, "Confirmed")]
    [InlineData(true, "FailedOnChain")]
    public async Task FinalizedResult_RequiresExactPersistedWireAndHistory(bool failed, string expected)
    {
        using var fixture = new SolanaDurableFixture();
        var prepared = (await fixture.Provider.PrepareNftTransactionAsync(await fixture.QuotedMintAsync())).Result!;
        await fixture.Provider.SubmitNftTransactionAsync(prepared);
        fixture.Finalized = true; fixture.Failed = failed;
        (await fixture.Provider.ObserveNftTransactionAsync(prepared)).Result!.Status.Should().Be(expected);
        fixture.OmitHistory = true;
        (await fixture.Provider.ObserveNftTransactionAsync(prepared)).Result!.Status.Should().Be("NeedsOperator");
        fixture.Sends.Should().Be(1);
    }

    [Fact]
    public async Task NativeValue_LostResponsePreservesIdentity_AndIntentGenerationIsSigned()
    {
        using var fixture = new SolanaDurableFixture();
        var result = await fixture.Provider.PrepareValueTransferAsync(fixture.Value(), default);
        result.IsError.Should().BeFalse(result.Message); fixture.Sends.Should().Be(0);
        var prepared = result.Result!;
        fixture.LoseResponse = true;
        Func<Task> send = () => fixture.Provider.SubmitValueTransferAsync(prepared, default);
        await send.Should().ThrowAsync<HttpRequestException>();
        fixture.Sends.Should().Be(1);
        fixture.Finalized = true;
        (await fixture.Provider.ObserveValueTransferAsync(prepared, default)).Result.Should().Be(ChainConfirmation.Confirmed);
        Func<Task> changed = () => fixture.Provider.SubmitValueTransferAsync(prepared with { Intent = prepared.Intent with { PreparationKey = "value:claim:1" } }, default);
        await changed.Should().ThrowAsync<InvalidOperationException>();
        fixture.Sends.Should().Be(1);
    }

    [Fact]
    public async Task WrongGenesis_PreventsSigningAndSubmission()
    {
        using var fixture = new SolanaDurableFixture(); fixture.GenesisResponse = fixture.Recipient.PublicKey.Key;
        Func<Task> prepare = () => fixture.Provider.PrepareValueTransferAsync(fixture.Value(), default);
        await prepare.Should().ThrowAsync<InvalidOperationException>();
        fixture.Consents.Should().BeEmpty(); fixture.Sends.Should().Be(0);
    }
}
