using System.Buffers.Binary;
using System.Globalization;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Helpers;

/// <summary>Stable redemption partition independent of signatures and mutable deployed implementation code.</summary>
public static class WttBodyClaimIdentity
{
    public static string Partition(WttDeploymentIdentity identity)
    {
        if (identity is null || identity.ProtocolChain == 0 || !CanonicalWord(identity.GenesisHex)
            || !CanonicalWord(identity.CoreBridgeHex) || !CanonicalWord(identity.TokenBridgeHex))
            throw new ArgumentException("Canonical protocol chain and fixed-width deployment bytes are required.", nameof(identity));
        return CanonicalJson.Hash(new { Version = 1, Deployment = identity });
    }

    public static string Id(string partition, string bodyDigest)
        => CanonicalJson.Hash(new { Version = 1, TargetPartition = partition, BodyDigest = bodyDigest });

    public static bool ValidPublication(WttBodyClaim claim)
    {
        if (string.IsNullOrEmpty(claim.BodyHex)) return false;
        byte[] body;
        try { body = Convert.FromHexString(claim.BodyHex); }
        catch (FormatException) { return false; }
        return body.Length is >= 52 and <= 65536 && Convert.ToHexStringLower(body) == claim.BodyHex
            && Convert.ToHexStringLower(Keccak256.ComputeHash(Keccak256.ComputeHash(body))) == claim.BodyDigest
            && BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(8)) == claim.EmitterChain
            && Convert.ToHexStringLower(body.AsSpan(10, 32)) == claim.EmitterAddress
            && BinaryPrimitives.ReadUInt64BigEndian(body.AsSpan(42)).ToString(CultureInfo.InvariantCulture) == claim.Sequence;
    }

    public static bool SameOwner(WttBodyClaim left, WttBodyClaim right)
        => left.Id == right.Id && left.TargetPartition == right.TargetPartition
            && left.TargetProtocolChain == right.TargetProtocolChain && left.TargetGenesisHex == right.TargetGenesisHex
            && left.TargetCoreBridgeHex == right.TargetCoreBridgeHex && left.TargetTokenBridgeHex == right.TargetTokenBridgeHex
            && left.TargetProtocolIdentity == right.TargetProtocolIdentity && left.TargetCoreBridge == right.TargetCoreBridge
            && left.TargetTokenBridge == right.TargetTokenBridge && left.SourceProtocolIdentity == right.SourceProtocolIdentity
            && left.EmitterChain == right.EmitterChain && left.EmitterAddress == right.EmitterAddress && left.Sequence == right.Sequence
            && left.BodyDigest == right.BodyDigest && left.BodyHex == right.BodyHex && left.OperationId == right.OperationId
            && left.IntentHash == right.IntentHash && left.ActorUserId == right.ActorUserId && left.TenantId == right.TenantId;

    private static bool CanonicalWord(string value)
        => value is { Length: 64 } && value.Any(character => character != '0')
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
