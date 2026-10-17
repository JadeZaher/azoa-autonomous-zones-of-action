using System.Buffers.Binary;
using System.Globalization;
using Algorand;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record WttAlgorandTargetParameters(string Stage, string PreparationKey, string Payer,
    ulong CoreApplication, ulong TokenApplication, ulong Asset, string Recipient, WttVerifiedProof Proof,
    ulong MinimumFee, ulong RegularFee, ulong VerificationFee, ulong CompletionFee,
    ulong FirstValid, ulong LastValid, string GenesisHash, string GenesisId);

/// <summary>Exact verifySigs, verifyVAA and registration/redemption group from the pinned Wormhole processor.</summary>
internal static class WttAlgorandTargetWire
{
    internal const string VerifierAddress = "EZATROXX2HISIRZDRGXW4LRQ46Z6IUJYYIHU3PJGP7P5IQDPKVX42N767A";
    private static readonly byte[] VerifierProgram = [
        6, 32, 4, 1, 0, 32, 20, 38, 1, 0, 49, 32, 50, 3, 18, 68, 49, 1, 35, 18, 68,
        49, 16, 129, 6, 18, 68, 54, 26, 1, 54, 26, 3, 54, 26, 2, 136, 0, 3, 68, 34,
        67, 53, 2, 53, 1, 53, 0, 40, 53, 240, 40, 53, 241, 52, 0, 21, 53, 5, 35, 53,
        3, 35, 53, 4, 52, 3, 52, 5, 12, 65, 0, 68, 52, 1, 52, 0, 52, 3, 129, 65, 8,
        34, 88, 23, 52, 0, 52, 3, 34, 8, 36, 88, 52, 0, 52, 3, 129, 33, 8, 36, 88, 7,
        0, 53, 241, 53, 240, 52, 2, 52, 4, 37, 88, 52, 240, 52, 241, 80, 2, 87, 12,
        20, 18, 68, 52, 3, 129, 66, 8, 53, 3, 52, 4, 37, 8, 53, 4, 66, 255, 180, 34, 137];

    internal static IReadOnlyList<Transaction> Build(WttAlgorandTargetParameters input)
    {
        if (input.Stage is not ("Register" or "Redeem") || input.CoreApplication == 0 || input.TokenApplication == 0
            || input.CoreApplication == input.TokenApplication || input.MinimumFee == 0 || input.RegularFee < input.MinimumFee
            || input.Proof?.Vaa is null || input.Proof.GuardianEvidence is null
            || WttAlgorandStorage.ProgramAddress(VerifierProgram) != VerifierAddress)
            throw new InvalidDataException("Invalid Algorand target protocol and stage.");
        var envelope = Convert.FromHexString(input.Proof.Vaa.EnvelopeHex);
        var vaa = WttVaaCodec.Parse(envelope);
        if (envelope.Length + (input.Stage == "Register" ? 13 : 16) > 2048)
            throw new InvalidDataException("VAA exceeds the application argument capacity of this protocol transaction.");
        if (CanonicalJson.Hash(vaa) != CanonicalJson.Hash(input.Proof.Vaa)
            || vaa.EmitterChain == 8 || input.Proof.GuardianEvidence.GuardianSetIndex != vaa.GuardianSetIndex
            || input.Proof.GuardianEvidence.ProtocolIdentity != "algorand:" + input.GenesisHash
            || input.Proof.GuardianEvidence.CoreBridge != input.CoreApplication.ToString(CultureInfo.InvariantCulture)
            || input.Proof.GuardianEvidence.TokenBridge != input.TokenApplication.ToString(CultureInfo.InvariantCulture)
            || input.Proof.GuardianEvidence.RegisteredEmitterChain != vaa.EmitterChain
            || input.Proof.GuardianEvidence.RegisteredEmitterAddress != vaa.EmitterAddress)
            throw new InvalidDataException("Target VAA envelope differs from its exact verified proof.");
        var keys = input.Proof.GuardianEvidence.GuardianAddresses;
        if (keys is null || keys.Count is < 1 or > 32 || keys.Any(key => key is not { Length: 40 } || !key.All(char.IsAsciiHexDigitLower))
            || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count || vaa.Signatures.Count <= keys.Count * 2 / 3
            || vaa.Signatures.Any(signature => signature.GuardianIndex >= keys.Count))
            throw new InvalidDataException("Target signature group requires unique canonical guardian keys and quorum.");
        var batches = (vaa.Signatures.Count + 5) / 6;
        if ((UInt128)input.VerificationFee < (UInt128)input.MinimumFee * (uint)(batches + 1))
            throw new InvalidDataException("Target verification fee does not cover all stateless group members.");
        var payer = new Address(input.Payer);
        var sequence = new Address(SequenceAddress(input.TokenApplication, vaa));
        var guardian = new Address(WttAlgorandStorage.Address(input.CoreApplication, vaa.GuardianSetIndex, "guardian"u8));
        var transactions = new List<Transaction>();
        for (var index = 0; index < batches; index++)
        {
            var signatures = vaa.Signatures.Skip(index * 6).Take(6).ToArray();
            transactions.Add(new ApplicationNoopTransaction { Sender = new Address(VerifierAddress), ApplicationId = input.CoreApplication,
                ApplicationArgs = ["verifySigs"u8.ToArray(), envelope.AsSpan(6 + index * 396, signatures.Length * 66).ToArray(),
                    signatures.SelectMany(signature => Convert.FromHexString(keys[signature.GuardianIndex])).ToArray(), Convert.FromHexString(vaa.BodyDigest)],
                Accounts = [sequence, guardian] });
        }
        transactions.Add(new ApplicationNoopTransaction { Sender = payer, ApplicationId = input.CoreApplication,
            ApplicationArgs = ["verifyVAA"u8.ToArray(), envelope], Accounts = [sequence, guardian] });
        if (input.Stage == "Register")
        {
            var metadata = WttPayloadCodec.ParseAttestation(Convert.FromHexString(vaa.PayloadHex));
            if (metadata.TokenChain is 0 or 8 || (UInt128)input.CompletionFee < (UInt128)input.MinimumFee * 2)
                throw new InvalidDataException("Registration requires a foreign original asset and its inner creation fee.");
            var storage = new Address(WttAlgorandStorage.Address(input.TokenApplication, metadata.TokenChain, Convert.FromHexString(metadata.TokenAddress)));
            transactions.Add(new PaymentTransaction { Sender = payer, Receiver = storage, Amount = 100000 });
            transactions.Add(new ApplicationNoopTransaction { Sender = payer, ApplicationId = input.TokenApplication, ApplicationArgs = ["nop"u8.ToArray(), [1]] });
            transactions.Add(new ApplicationNoopTransaction { Sender = payer, ApplicationId = input.TokenApplication, ApplicationArgs = ["nop"u8.ToArray(), [2]] });
            transactions.Add(new ApplicationNoopTransaction { Sender = payer, ApplicationId = input.TokenApplication,
                ApplicationArgs = ["receiveAttest"u8.ToArray(), envelope], Accounts = [sequence, guardian, storage],
                ForeignAssets = input.Asset == 0 ? [] : [input.Asset] });
        }
        else
        {
            var transfer = WttPayloadCodec.ParseTransfer(Convert.FromHexString(vaa.PayloadHex));
            var recipient = new Address(input.Recipient);
            var fee = UInt64Text.Parse(transfer.Fee);
            _ = UInt64Text.Parse(transfer.Amount);
            if (transfer.RecipientChain != 8 || transfer.Recipient != Convert.ToHexStringLower(recipient.Bytes)
                || (UInt128)input.CompletionFee < (UInt128)input.MinimumFee * (fee == 0 ? 2u : 3u))
                throw new InvalidDataException("Redemption recipient or inner transfer fee differs from the exact payload.");
            string storage;
            if (transfer.TokenChain == 8)
            {
                var origin = Convert.FromHexString(transfer.TokenAddress);
                if (origin.AsSpan(0, 24).IndexOfAnyExcept((byte)0) >= 0 || BinaryPrimitives.ReadUInt64BigEndian(origin.AsSpan(24)) != input.Asset)
                    throw new InvalidDataException("Original Algorand asset is not the exact lossless ASA ID.");
                storage = WttAlgorandStorage.Address(input.TokenApplication, input.Asset, "native"u8);
            }
            else
            {
                if (input.Asset == 0) throw new InvalidDataException("Foreign wrapped token must be registered before redemption.");
                storage = WttAlgorandStorage.Address(input.TokenApplication, transfer.TokenChain, Convert.FromHexString(transfer.TokenAddress));
            }
            transactions.Add(new ApplicationNoopTransaction { Sender = payer, ApplicationId = input.TokenApplication,
                ApplicationArgs = ["completeTransfer"u8.ToArray(), envelope], Accounts = [sequence, guardian, new Address(storage), recipient],
                ForeignAssets = input.Asset == 0 ? [] : [input.Asset] });
        }
        WttAlgorandGroup.Seal(transactions, "azoa:wtt:target:v1:", input.PreparationKey, input.FirstValid, input.LastValid,
            input.GenesisHash, input.GenesisId, index => index < batches ? 0 : index == batches ? input.VerificationFee
                : index == transactions.Count - 1 ? input.CompletionFee : input.RegularFee);
        return transactions;
    }

    internal static string SequenceAddress(ulong tokenApplication, WttVaa vaa)
    {
        var emitter = new byte[34]; BinaryPrimitives.WriteUInt16BigEndian(emitter, vaa.EmitterChain);
        Convert.FromHexString(vaa.EmitterAddress).CopyTo(emitter, 2);
        return WttAlgorandStorage.Address(tokenApplication, UInt64Text.Parse(vaa.Sequence) / (127 * 15 * 8), emitter);
    }

    internal static SignedTransaction ProtocolEnvelope(WttAlgorandTargetParameters input, int index)
    {
        var group = Build(input);
        if (index < 0 || index >= (input.Proof.Vaa.Signatures.Count + 5) / 6)
            throw new InvalidDataException("Only verification group members use the stateless protocol signature.");
        return new(group[index], null, null, new LogicsigSignature((byte[])VerifierProgram.Clone()), null);
    }

    internal static IReadOnlyList<SignedTransaction> ValidateSigned(WttAlgorandTargetParameters input, IReadOnlyList<string> envelopes)
    {
        var expected = Build(input);
        if (envelopes.Count != expected.Count) throw new InvalidDataException("Incomplete target group.");
        var batches = (input.Proof.Vaa.Signatures.Count + 5) / 6;
        var result = new List<SignedTransaction>();
        for (var index = 0; index < envelopes.Count; index++)
        {
            var signed = AlgorandSignedEnvelope.DecodeCanonical(envelopes[index]);
            if (index < batches)
            {
                if (!Encoder.EncodeToMsgPackOrdered(signed).AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(ProtocolEnvelope(input, index))))
                    throw new InvalidDataException("Target verification must use the exact undelegated reviewed stateless program.");
            }
            else
            {
                if (!Encoder.EncodeToMsgPackOrdered(signed.Tx).AsSpan().SequenceEqual(Encoder.EncodeToMsgPackOrdered(expected[index])))
                    throw new InvalidDataException("Target payer transaction differs from its immutable group.");
                AlgorandSignedEnvelope.RequireSingleSender(signed, input.Payer);
            }
            result.Add(signed);
        }
        return result;
    }
}
