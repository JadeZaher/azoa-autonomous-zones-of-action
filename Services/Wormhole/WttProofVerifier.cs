using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces.Wtt;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Wtt;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Verifies exact published bodies against fresh target guardian and emitter state.</summary>
public sealed class WttProofVerifier(IWttGuardianSignatureVerifier signatures, TimeProvider clock,
    IOptions<WttFinalityOptions> finality)
{
    /// <summary>Applies the same trusted finalized-head age policy to already-posted canonical protocol evidence.</summary>
    public bool HasFreshFinalizedSnapshot(string protocolIdentity, long chainTimeUnixSeconds, DateTimeOffset observedAt)
        => FinalityError(protocolIdentity, chainTimeUnixSeconds, observedAt, clock.GetUtcNow()) is null;

    public async Task<AZOAResult<WttVerifiedProof>> VerifyAsync(string envelopeBase64, WttProofExpectation expected,
        IWttGuardianStateSource targetState, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expected.TargetProtocolIdentity) || string.IsNullOrWhiteSpace(expected.TargetCoreBridge)
            || string.IsNullOrWhiteSpace(expected.TargetTokenBridge)) return Denied("An explicit trusted target deployment is required.");
        WttVaa vaa;
        try
        {
            vaa = WttVaaCodec.ParseBase64(envelopeBase64);
            var payload = Convert.FromHexString(vaa.PayloadHex);
            if (payload.Length != 0 && payload[0] == 1) _ = WttPayloadCodec.ParseTransfer(payload);
            else _ = WttPayloadCodec.ParseAttestation(payload);
        }
        catch (FormatException) { return Denied("Malformed WTT VAA envelope."); }
        if (vaa.BodyHex != expected.SourceBodyHex)
            return Denied("VAA body does not match the exact finalized source publication.");
        var read = await targetState.ReadWttGuardianEvidenceAsync(vaa.GuardianSetIndex, vaa.EmitterChain, vaa.EmitterAddress, ct);
        if (read.IsError || read.Result is null) return Denied(read.Message);
        var evidence = read.Result;
        var now = clock.GetUtcNow();
        var freshnessError = FinalityError(expected.TargetProtocolIdentity, evidence.ChainTimeUnixSeconds, evidence.ObservedAt, now);
        if (freshnessError is not null) return Denied(freshnessError);
        if (evidence.ProtocolIdentity != expected.TargetProtocolIdentity || evidence.CoreBridge != expected.TargetCoreBridge
            || evidence.TokenBridge != expected.TargetTokenBridge || string.IsNullOrWhiteSpace(evidence.BlockIdentity)
            || evidence.GuardianSetIndex != vaa.GuardianSetIndex
            || evidence.GuardianSetIndex > evidence.CurrentGuardianSetIndex
            || evidence.RegisteredEmitterChain != vaa.EmitterChain || evidence.RegisteredEmitterAddress != vaa.EmitterAddress)
            return Denied("Guardian or emitter state does not match the frozen target deployment and source publication.");
        if (evidence.GuardianSetIndex != evidence.CurrentGuardianSetIndex
            && evidence.ExpirationUnixSeconds <= Math.Max(evidence.ChainTimeUnixSeconds, now.ToUnixTimeSeconds()))
            return Denied("The signing guardian set has expired on the target chain.");
        var guardians = evidence.GuardianAddresses;
        if (guardians is null || guardians.Count is 0 or > 256 || guardians.Distinct(StringComparer.Ordinal).Count() != guardians.Count)
            return Denied("Authoritative guardian set is empty, oversized or contains duplicate keys.");
        var quorum = guardians.Count * 2 / 3 + 1;
        if (vaa.Signatures.Count < quorum) return Denied("VAA has fewer signatures than the authoritative guardian quorum.");
        byte[][] addresses;
        try { addresses = guardians.Select(address => WttPayloadCodec.FixedHex(address, 20)).ToArray(); }
        catch (FormatException) { return Denied("Authoritative guardian address encoding is invalid."); }
        if (addresses.Select(address => Convert.ToHexStringLower(address)).Distinct(StringComparer.Ordinal).Count() != addresses.Length)
            return Denied("Authoritative guardian set contains duplicate decoded keys.");
        var digest = Convert.FromHexString(vaa.BodyDigest);
        foreach (var signature in vaa.Signatures)
        {
            ct.ThrowIfCancellationRequested();
            if (signature.GuardianIndex >= addresses.Length || !signatures.VerifyGuardianSignature(digest,
                new WormholeVaaSignature { GuardianIndex = signature.GuardianIndex, R = Convert.FromHexString(signature.R),
                    S = Convert.FromHexString(signature.S), V = signature.RecoveryId }, addresses[signature.GuardianIndex]))
                return Denied("A guardian signature does not match the authoritative set.");
        }
        return AZOAResult<WttVerifiedProof>.Success(new(vaa, evidence));
    }

    private string? FinalityError(string protocolIdentity, long chainTimeUnixSeconds, DateTimeOffset observedAt, DateTimeOffset now)
    {
        var policies = (finality.Value.Networks ?? []).Where(policy => policy is not null && policy.ProtocolIdentity == protocolIdentity).ToArray();
        if (policies.Length != 1 || policies[0].MaximumLagSeconds <= 0)
            return "Exactly one server-owned finalized-head age policy is required for this target network.";
        if (chainTimeUnixSeconds > now.AddSeconds(5).ToUnixTimeSeconds()
            || chainTimeUnixSeconds < now.AddSeconds(-policies[0].MaximumLagSeconds).ToUnixTimeSeconds()
            || observedAt > now.AddSeconds(5) || observedAt < now.AddMinutes(-2))
            return "The target finalized chain snapshot is stale or ahead of the trusted clock.";
        return null;
    }

    private static AZOAResult<WttVerifiedProof> Denied(string message) => new() { IsError = true, Message = message };
}
