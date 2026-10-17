using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;

namespace AZOA.WebAPI.Services.Wormhole;

/// <summary>Checks canonical proof bytes against frozen transfer economics; source finality and guardian verification remain separate.</summary>
public static class WttProofBinding
{
    public static WttVaa Transfer(WttOperationIntent intent, WttVerifiedProof proof)
    {
        var vaa = Canonical(proof);
        var payload = WttPayloadCodec.ParseTransfer(Convert.FromHexString(vaa.PayloadHex));
        if (WttPayloadCodec.Normalize(intent.Amount.Requested, WttPayloadCodec.Denormalize(intent.Amount.Fee, intent.Asset.Decimals),
                intent.Asset.Decimals) != intent.Amount
            || vaa.EmitterChain != intent.Source.ProtocolChain
            || vaa.EmitterAddress != intent.Source.EmitterAddressHex
            || vaa.Nonce != intent.Nonce || vaa.ConsistencyLevel != intent.ConsistencyLevel
            || payload != new WttTransferPayload(intent.Amount.Amount, intent.Asset.OriginAddress, intent.Asset.OriginChain,
                intent.RecipientProtocolAddress, intent.Target.ProtocolChain, intent.Amount.Fee))
            throw new InvalidDataException("WTT transfer proof differs from the frozen source, origin, recipient or amount.");
        return vaa;
    }

    public static WttVaa Attestation(WttOperationIntent intent, WttVerifiedProof proof)
    {
        var vaa = Canonical(proof);
        var payload = WttPayloadCodec.ParseAttestation(Convert.FromHexString(vaa.PayloadHex));
        if (vaa.EmitterChain != intent.Asset.OriginChain || payload.TokenChain != intent.Asset.OriginChain
            || payload.TokenAddress != intent.Asset.OriginAddress
            || Math.Min(payload.Decimals, (byte)8) != Math.Min(intent.Asset.Decimals, (byte)8)
            || intent.Asset.OriginDecimals is { } originalDecimals && payload.Decimals != originalDecimals)
            throw new InvalidDataException("WTT registration proof does not attest the exact original asset and precision.");
        return vaa;
    }

    private static WttVaa Canonical(WttVerifiedProof proof)
    {
        if (proof?.Vaa is null || string.IsNullOrEmpty(proof.Vaa.EnvelopeHex))
            throw new InvalidDataException("WTT canonical proof bytes are missing.");
        var parsed = WttVaaCodec.Parse(Convert.FromHexString(proof.Vaa.EnvelopeHex));
        if (CanonicalJson.Hash(parsed) != CanonicalJson.Hash(proof.Vaa))
            throw new InvalidDataException("WTT parsed proof fields differ from its canonical envelope.");
        return parsed;
    }
}
