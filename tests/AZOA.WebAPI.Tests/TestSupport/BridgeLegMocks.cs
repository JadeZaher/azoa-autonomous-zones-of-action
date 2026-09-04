using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Bridge;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// Mocks for the two bridge capability modules, plus the <c>TryGetModule</c> wiring that makes a
/// provider mock resolve them.
/// </summary>
/// <remarks>
/// Bridge capability is expressed by MODULE PRESENCE, so a test declares capability by attaching
/// legs and declares the fail-closed case by attaching none. There is no boolean to flip, which
/// is the point: a test cannot accidentally assert a capability the provider does not have.
/// </remarks>
public sealed class BridgeLegMocks
{
    public Mock<IBridgeSourceLeg> Source { get; } = new();
    public Mock<IBridgeTargetLeg> Target { get; } = new();

    /// <summary>The source lock returns a real transaction id (optionally still unconfirmed).</summary>
    public BridgeLegMocks LockReturns(string transactionId, bool pending = false)
    {
        Source.Setup(leg => leg.LockAsync(It.IsAny<BridgeLockRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<BridgeLock>
            {
                IsError = false,
                Result = new BridgeLock(transactionId, pending, transactionId),
            });
        return this;
    }

    /// <summary>The source lock fails loud.</summary>
    public BridgeLegMocks LockFails(string message)
    {
        Source.Setup(leg => leg.LockAsync(It.IsAny<BridgeLockRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<BridgeLock> { IsError = true, Message = message });
        return this;
    }

    /// <summary>The source release returns a real transaction id (optionally pending confirmation).</summary>
    public BridgeLegMocks ReleaseReturns(string transactionId, bool pending = false)
    {
        Source.Setup(leg => leg.ReleaseAsync(It.IsAny<BridgeReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StringResult(transactionId, pending));
        return this;
    }

    /// <summary>The source release fails loud.</summary>
    public BridgeLegMocks ReleaseFails(string message)
    {
        Source.Setup(leg => leg.ReleaseAsync(It.IsAny<BridgeReleaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = true, Message = message });
        return this;
    }

    /// <summary>The target wrapped mint returns a real transaction id (optionally pending).</summary>
    public BridgeLegMocks MintReturns(string transactionId, bool pending = false)
    {
        Target.Setup(leg => leg.MintWrappedAsync(It.IsAny<BridgeMintRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StringResult(transactionId, pending));
        return this;
    }

    /// <summary>The target wrapped mint fails loud.</summary>
    public BridgeLegMocks MintFails(string message)
    {
        Target.Setup(leg => leg.MintWrappedAsync(It.IsAny<BridgeMintRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = true, Message = message });
        return this;
    }

    /// <summary>The target wrapped burn returns a real transaction id (optionally pending).</summary>
    public BridgeLegMocks BurnReturns(string transactionId, bool pending = false)
    {
        Target.Setup(leg => leg.BurnWrappedAsync(It.IsAny<BridgeBurnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(StringResult(transactionId, pending));
        return this;
    }

    /// <summary>The target wrapped burn fails loud.</summary>
    public BridgeLegMocks BurnFails(string message)
    {
        Target.Setup(leg => leg.BurnWrappedAsync(It.IsAny<BridgeBurnRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<string> { IsError = true, Message = message });
        return this;
    }

    public void VerifyLockTimes(Func<Times> times)
        => Source.Verify(
            leg => leg.LockAsync(It.IsAny<BridgeLockRequest>(), It.IsAny<CancellationToken>()), times);

    public void VerifyReleaseTimes(Func<Times> times)
        => Source.Verify(
            leg => leg.ReleaseAsync(It.IsAny<BridgeReleaseRequest>(), It.IsAny<CancellationToken>()), times);

    public void VerifyMintTimes(Func<Times> times)
        => Target.Verify(
            leg => leg.MintWrappedAsync(It.IsAny<BridgeMintRequest>(), It.IsAny<CancellationToken>()), times);

    public void VerifyBurnTimes(Func<Times> times)
        => Target.Verify(
            leg => leg.BurnWrappedAsync(It.IsAny<BridgeBurnRequest>(), It.IsAny<CancellationToken>()), times);

    private static AZOAResult<string> StringResult(string transactionId, bool pending)
        => new()
        {
            IsError = false,
            Result = transactionId,
            Message = pending
                ? $"{OperationStatus.PendingConfirmationMarker}: {transactionId}"
                : transactionId,
        };
}

/// <summary>Attaches bridge capability modules to a provider mock through <c>TryGetModule</c>.</summary>
public static class BridgeProviderMockExtensions
{
    /// <summary>
    /// Makes the provider mock resolve the requested bridge legs. Omitting a leg leaves
    /// <c>TryGetModule</c> returning false for it — the structural fail-closed case.
    /// </summary>
    public static BridgeLegMocks WithBridgeLegs(
        this Mock<IBlockchainProvider> provider, bool source = true, bool target = true)
    {
        var legs = new BridgeLegMocks();

        if (source)
        {
            IBridgeSourceLeg? sourceLeg = legs.Source.Object;
            provider.Setup(item => item.TryGetModule(out sourceLeg)).Returns(true);
        }

        if (target)
        {
            IBridgeTargetLeg? targetLeg = legs.Target.Object;
            provider.Setup(item => item.TryGetModule(out targetLeg)).Returns(true);
        }

        return legs;
    }
}
