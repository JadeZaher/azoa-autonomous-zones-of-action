using Algorand;
using AlgoAccount = Algorand.Algod.Model.Account;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// Wallet-side Algorand signing over the exact unsigned bytes. The SDK cannot round-trip a zero-amount axfer
/// (it decodes as an opt-in and drops <c>aclose</c>), so tests sign and inspect raw msgpack instead.
/// </summary>
internal static class AlgorandExactWire
{
    /// <summary>Canonical <c>{sig, txn}</c> envelope over <paramref name="unsignedTransaction"/>, as algosdk emits it.</summary>
    internal static string Sign(byte[] unsignedTransaction, AlgoAccount account)
    {
        var signature = account.SignRawBytes([.. "TX"u8, .. unsignedTransaction]).Bytes;
        return Convert.ToBase64String([0x82, 0xa3, (byte)'s', (byte)'i', (byte)'g', 0xc4, 0x40, .. signature,
            0xa3, (byte)'t', (byte)'x', (byte)'n', .. unsignedTransaction]);
    }

    /// <summary>True when the msgpack map carries <paramref name="key"/> bound to the 32-byte address.</summary>
    internal static bool HasAddressField(byte[] transaction, string key, string address)
    {
        byte[] field = [(byte)(0xa0 + key.Length), .. System.Text.Encoding.ASCII.GetBytes(key), 0xc4, 0x20, .. new Address(address).Bytes];
        return transaction.AsSpan().IndexOf(field) >= 0;
    }

    /// <summary>True when the msgpack map carries <paramref name="key"/> at all.</summary>
    internal static bool HasField(byte[] transaction, string key)
        => transaction.AsSpan().IndexOf([(byte)(0xa0 + key.Length), .. System.Text.Encoding.ASCII.GetBytes(key)]) >= 0;
}
