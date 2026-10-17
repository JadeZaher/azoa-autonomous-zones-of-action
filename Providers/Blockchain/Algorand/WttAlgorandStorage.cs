extern alias BCCrypto2;
using System.Buffers.Binary;
using System.Text.Json;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Services.Wormhole;
using Sha512tDigest = BCCrypto2::Org.BouncyCastle.Crypto.Digests.Sha512tDigest;

namespace AZOA.WebAPI.Providers.Blockchain.Algorand;

/// <summary>Canonical Wormhole storage accounts and local blob framing; see provider notes.</summary>
internal static class WttAlgorandStorage
{
    internal const int PageSize = 127;
    internal const int PageCount = 15;
    internal sealed record Blob(byte[] Bytes, byte[] Metadata);
    internal sealed record Guardians(IReadOnlyList<string> Addresses, ulong ExpiresAt);
    internal sealed record WrappedAsset(ulong AssetId, WttAttestationPayload Origin, ushort EmitterChain, string EmitterAddress);

    internal static byte[] Program(ulong application, ulong index, ReadOnlySpan<byte> emitter)
    {
        if (application == 0 || emitter.Length is 0 or > 64)
            throw new ArgumentException("Wormhole storage requires an application and a bounded emitter.");
        using var bytes = new MemoryStream();
        bytes.Write(Convert.FromHexString("0620010181"));
        WriteUnsigned(bytes, index);
        bytes.Write(Convert.FromHexString("4880"));
        WriteUnsigned(bytes, (ulong)emitter.Length);
        bytes.Write(emitter);
        bytes.Write(Convert.FromHexString("483110810612443119221244311881"));
        WriteUnsigned(bytes, application);
        bytes.Write(Convert.FromHexString("1244312080"));
        var authority = global::Algorand.Address.ForApplication(application).Bytes;
        WriteUnsigned(bytes, (ulong)authority.Length);
        bytes.Write(authority);
        bytes.Write(Convert.FromHexString("124431018100124431093203124431153203124422"));
        return bytes.ToArray();
    }

    internal static string Address(ulong application, ulong index, ReadOnlySpan<byte> emitter)
        => ProgramAddress(Program(application, index, emitter));

    internal static string ProgramAddress(byte[] program)
    {
        var digest = new Sha512tDigest(256);
        digest.BlockUpdate("Program"u8.ToArray(), 0, 7);
        digest.BlockUpdate(program, 0, program.Length);
        var hash = new byte[32];
        digest.DoFinal(hash, 0);
        return new global::Algorand.Address(hash).ToString();
    }

    internal static bool IsUnallocated(JsonElement account, string address, ulong round)
        => Text(account, "address", out var actual) && actual == address
            && Unsigned(account, "round", out var at) && at == round
            && (!account.TryGetProperty("auth-addr", out var authority)
                || authority.ValueKind == JsonValueKind.String && authority.GetString() == address)
            && (!account.TryGetProperty("apps-local-state", out var states)
                || states.ValueKind == JsonValueKind.Array && states.GetArrayLength() == 0);

    internal static Blob? Read(JsonElement account, ulong application, string expectedAddress, ulong round)
    {
        if (application == 0 || round == 0 || account.ValueKind != JsonValueKind.Object) return null;
        try
        {
            _ = CanonicalJson.Canonicalize(account);
            if (!Text(account, "address", out var address) || address != expectedAddress
                || !Text(account, "auth-addr", out var authority)
                || authority != global::Algorand.Address.ForApplication(application).ToString()
                || !Unsigned(account, "round", out var actualRound) || actualRound != round
                || !account.TryGetProperty("apps-local-state", out var states) || states.ValueKind != JsonValueKind.Array)
                return null;
            var matches = states.EnumerateArray().Where(state => Unsigned(state, "id", out var id) && id == application).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("key-value", out var entries)
                || entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() < PageCount || entries.GetArrayLength() > PageCount + 1) return null;
            var pages = new byte[PageCount][];
            byte[]? metadata = null;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!Text(entry, "key", out var encodedKey) || encodedKey.Length > 8
                    || !entry.TryGetProperty("value", out var value)
                    || !Unsigned(value, "type", out var type) || type != 1
                    || !Text(value, "bytes", out var encodedValue) || encodedValue.Length > 172) return null;
                var key = Convert.FromBase64String(encodedKey);
                var data = Convert.FromBase64String(encodedValue);
                if (Convert.ToBase64String(key) != encodedKey || Convert.ToBase64String(data) != encodedValue) return null;
                if (key.AsSpan().SequenceEqual("meta"u8))
                {
                    if (metadata is not null) return null;
                    metadata = data;
                }
                else
                {
                    if (key.Length != 1 || key[0] >= PageCount || pages[key[0]] is not null || data.Length != PageSize) return null;
                    pages[key[0]] = data;
                }
            }
            if (pages.Any(page => page is null)) return null;
            var blob = new byte[PageSize * PageCount];
            for (var index = 0; index < PageCount; index++) pages[index].CopyTo(blob, index * PageSize);
            return new(blob, metadata ?? []);
        }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    internal static bool IsSourceRole(Blob blob, bool emitter)
        => blob.Bytes.Length == PageSize * PageCount &&
            (blob.Metadata.Length == 0 && blob.Bytes.All(value => value == 0)
                || emitter && blob.Metadata.AsSpan().SequenceEqual("publishMessage"u8)
                    && blob.Bytes.AsSpan(8).IndexOfAnyExcept((byte)0) < 0);

    internal static Guardians? ReadGuardians(Blob blob)
    {
        if (blob.Bytes.Length != PageSize * PageCount || !blob.Metadata.AsSpan().SequenceEqual("guardian"u8)) return null;
        var count = blob.Bytes[0];
        if (count == 0 || 1 + count * 20 > 1000) return null;
        var addresses = new string[count];
        for (var index = 0; index < count; index++)
            addresses[index] = Convert.ToHexStringLower(blob.Bytes.AsSpan(1 + index * 20, 20));
        if (addresses.Any(address => address == new string('0', 40))
            || addresses.Distinct(StringComparer.Ordinal).Count() != addresses.Length) return null;
        return new(addresses, BinaryPrimitives.ReadUInt64BigEndian(blob.Bytes.AsSpan(1000, 8)));
    }

    internal static WrappedAsset? ReadWrappedAsset(Blob blob)
    {
        if (blob.Bytes.Length != PageSize * PageCount || !blob.Metadata.AsSpan().SequenceEqual("asset"u8)) return null;
        var asset = BinaryPrimitives.ReadUInt64BigEndian(blob.Bytes);
        var body = blob.Bytes.AsSpan(8, 151);
        var emitterChain = BinaryPrimitives.ReadUInt16BigEndian(body[8..]);
        if (asset == 0 || emitterChain is 0 or 8 || blob.Bytes.AsSpan(159).ContainsAnyExcept((byte)0)) return null;
        try
        {
            var origin = WttPayloadCodec.ParseAttestation(body[51..]);
            if (origin.TokenChain != emitterChain || origin.TokenAddress == new string('0', 64)) return null;
            return new(asset, origin, emitterChain, Convert.ToHexStringLower(body.Slice(10, 32)));
        }
        catch (FormatException) { return null; }
    }

    private static void WriteUnsigned(Stream bytes, ulong value)
    {
        do
        {
            var next = (byte)(value & 127);
            value >>= 7;
            bytes.WriteByte(value == 0 ? next : (byte)(next | 128));
        } while (value != 0);
    }

    private static bool Text(JsonElement value, string key, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) return false;
        text = field.GetString()!;
        return true;
    }

    private static bool Unsigned(JsonElement value, string key, out ulong number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            && field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out number);
    }
}
