namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Reads the token bridge's integer GetBit replay flag inside its canonical sequence storage page.</summary>
internal static class WttAlgorandReplayBitmap
{
    internal static bool IsConsumed(WttAlgorandStorage.Blob blob, ulong sequence)
    {
        if (blob.Bytes.Length != WttAlgorandStorage.PageCount * WttAlgorandStorage.PageSize || blob.Metadata.Length != 0)
            throw new InvalidDataException("Replay storage must have the exact unlabelled protocol bitmap layout.");
        var offset = (int)((sequence / 8) % (WttAlgorandStorage.PageCount * WttAlgorandStorage.PageSize));
        return (blob.Bytes[offset] & (1 << (int)(sequence % 8))) != 0;
    }
}
