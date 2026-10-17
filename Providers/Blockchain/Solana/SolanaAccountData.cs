using System.Text.Json;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

/// <summary>Reads exact base64 account data only after authoritative owner and executable-role checks.</summary>
public static class SolanaAccountData
{
    public static byte[] ReadOwned(JsonElement account, string owner, bool executable)
    {
        if (account.ValueKind != JsonValueKind.Object || !account.TryGetProperty("owner", out var actualOwner)
            || actualOwner.ValueKind != JsonValueKind.String || actualOwner.GetString() != owner || !account.TryGetProperty("executable", out var actualExecutable)
            || actualExecutable.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || actualExecutable.GetBoolean() != executable
            || !account.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 2
            || data[0].ValueKind != JsonValueKind.String || data[1].ValueKind != JsonValueKind.String || data[1].GetString() != "base64")
            throw new InvalidDataException("Solana account is absent or has the wrong owner, role or encoding.");
        try { return Convert.FromBase64String(data[0].GetString()!); }
        catch (FormatException) { throw new InvalidDataException("Solana account data is not base64."); }
    }
}
