using System.Buffers.Binary;
using System.Security.Cryptography;
using Algorand;
using Algorand.Algod.Model.Transactions;
using AZOA.WebAPI.Core.Blockchain;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

internal sealed record TinymanWireParameters(string Stage, string PreparationKey, string Owner, string Treasury,
    ulong Validator, ulong InputAsset, ulong OutputAsset, ulong GrossInput, ulong PlatformFee, ulong MinimumOutput,
    ulong FirstValid, ulong LastValid, string GenesisHash, string GenesisId, ulong MinimumFee, ulong RegularFee,
    ulong ApplicationFee);

/// <summary>Builds the closed Tinyman V2 fixed-input group grammar from immutable validated parameters.</summary>
internal static class TinymanSwapWire
{
    public static IReadOnlyList<Transaction> Build(TinymanWireParameters parameters)
    {
        if (parameters.Stage is not ("OptIn" or "Swap" or "CloseOptIn") || string.IsNullOrEmpty(parameters.PreparationKey) ||
            parameters.Validator == 0 || parameters.InputAsset == parameters.OutputAsset || parameters.GrossInput == 0 ||
            parameters.PlatformFee >= parameters.GrossInput || parameters.MinimumOutput == 0 || parameters.FirstValid == 0 ||
            parameters.LastValid < parameters.FirstValid || parameters.LastValid - parameters.FirstValid > 1000 ||
            parameters.MinimumFee == 0 || parameters.RegularFee < parameters.MinimumFee ||
            (UInt128)parameters.ApplicationFee < (UInt128)parameters.MinimumFee * 2)
            throw new ArgumentException("Invalid Tinyman wire parameters.", nameof(parameters));
        var genesis = Convert.FromBase64String(parameters.GenesisHash);
        if (genesis.Length != 32 || Convert.ToBase64String(genesis) != parameters.GenesisHash)
            throw new ArgumentException("Invalid Tinyman genesis identity.", nameof(parameters));
        var owner = new Address(parameters.Owner);
        var pool = new Address(TinymanV2PoolLocator.GetPoolAddress(parameters.Validator, parameters.InputAsset, parameters.OutputAsset));
        var transactions = new List<Transaction>();
        if (parameters.Stage is "OptIn" or "CloseOptIn")
        {
            if (parameters.OutputAsset == 0) throw new ArgumentException("Native currency needs no opt-in.", nameof(parameters));
            transactions.Add(new AssetTransferTransaction { Sender = owner, AssetReceiver = owner,
                XferAsset = parameters.OutputAsset, AssetAmount = 0, AssetCloseTo = parameters.Stage == "CloseOptIn" ? owner : null });
        }
        else
        {
            transactions.Add(Transfer(owner, pool, parameters.InputAsset, parameters.GrossInput - parameters.PlatformFee));
            var minimum = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(minimum, parameters.MinimumOutput);
            transactions.Add(new ApplicationNoopTransaction
            {
                Sender = owner, ApplicationId = parameters.Validator,
                ApplicationArgs = [System.Text.Encoding.UTF8.GetBytes("swap"), System.Text.Encoding.UTF8.GetBytes("fixed-input"), minimum],
                Accounts = [pool], ForeignAssets = [Math.Max(parameters.InputAsset, parameters.OutputAsset), Math.Min(parameters.InputAsset, parameters.OutputAsset)],
            });
            if (parameters.PlatformFee > 0)
                transactions.Add(Transfer(owner, new Address(parameters.Treasury), parameters.InputAsset, parameters.PlatformFee));
        }
        var note = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("azoa:dex:v1:" + parameters.PreparationKey));
        foreach (var transaction in transactions)
        {
            transaction.FirstValid = parameters.FirstValid;
            transaction.LastValid = parameters.LastValid;
            transaction.GenesisHash = new Digest(parameters.GenesisHash);
            transaction.GenesisId = parameters.GenesisId;
            transaction.Note = note;
            transaction.SetFee(transaction is ApplicationCallTransaction ? parameters.ApplicationFee : parameters.RegularFee);
        }
        if (transactions.Count > 1) TxGroup.AssignGroupID(transactions.ToArray());
        return transactions;
    }

    public static IReadOnlyList<SignedTransaction> ValidateSigned(TinymanWireParameters parameters, IReadOnlyList<string> envelopes)
    {
        var expected = Build(parameters);
        if (envelopes.Count != expected.Count) throw new ArgumentException("Tinyman group member count differs from its admitted plan.", nameof(envelopes));
        var result = new List<SignedTransaction>();
        for (var index = 0; index < expected.Count; index++)
        {
            result.Add(AlgorandSignedEnvelope.MatchExact(envelopes[index], expected[index], parameters.Owner));
        }
        return result;
    }

    private static Transaction Transfer(Address sender, Address recipient, ulong asset, ulong amount)
        => asset == 0 ? new PaymentTransaction { Sender = sender, Receiver = recipient, Amount = amount }
            : new AssetTransferTransaction { Sender = sender, AssetReceiver = recipient, XferAsset = asset, AssetAmount = amount };
}
