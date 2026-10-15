using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Nethereum.ABI.FunctionEncoding;
using Nethereum.ABI.Model;
using Nethereum.Util;

namespace AZOA.WebAPI.Providers.Blockchain.Ethereum;

/// <summary>Solidity ABI call encoding / return decoding, backed by Nethereum's encoders.</summary>
/// <remarks>See <c>Providers/Blockchain/Ethereum/AGENTS.md</c> §abi.</remarks>
public static class EthereumAbi
{
    private static readonly Sha3Keccack Keccak = new();
    private static readonly FunctionCallEncoder CallEncoder = new();
    private static readonly ParameterDecoder ReturnDecoder = new();

    // ─── ERC-20 canonical signatures ───

    public const string TransferSignature = "transfer(address,uint256)";
    public const string TransferFromSignature = "transferFrom(address,address,uint256)";
    public const string BalanceOfSignature = "balanceOf(address)";
    public const string TotalSupplySignature = "totalSupply()";
    public const string DecimalsSignature = "decimals()";
    public const string NameSignature = "name()";
    public const string SymbolSignature = "symbol()";
    public const string MintSignature = "mint(address,uint256)";
    public const string BurnSignature = "burn(uint256)";

    /// <summary>First four bytes of keccak-256 over the canonical signature, as bare hex.</summary>
    public static string Selector(string canonicalSignature) =>
        Keccak.CalculateHash(canonicalSignature)[..8];

    /// <summary>Encodes a call to <paramref name="canonicalSignature"/> as <c>0x</c>-prefixed calldata.</summary>
    public static string EncodeCall(string canonicalSignature, params object[] values)
    {
        var parsed = ParseSignature(canonicalSignature)
            ?? throw new ArgumentException($"Unsupported function signature '{canonicalSignature}'.", nameof(canonicalSignature));

        return EncodeCall(parsed, values);
    }

    /// <summary>Encodes a call from an already-parsed signature.</summary>
    public static string EncodeCall(AbiFunctionSignature signature, params object[] values)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(values);

        if (values.Length != signature.Parameters.Length)
            throw new ArgumentException(
                $"'{signature.Canonical}' takes {signature.Parameters.Length} argument(s) but {values.Length} were supplied.",
                nameof(values));

        var encoded = signature.Parameters.Length == 0
            ? CallEncoder.EncodeRequest(Selector(signature.Canonical))
            : CallEncoder.EncodeRequest(Selector(signature.Canonical), signature.Parameters, values);

        return encoded.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? encoded : "0x" + encoded;
    }

    /// <summary>Decodes the return data of a call into the declared output types.</summary>
    public static bool TryDecodeReturn(string? returnData, Parameter[] outputs, out List<ParameterOutput> decoded)
    {
        decoded = new List<ParameterOutput>();
        if (!EthereumHex.TryParseData(returnData, out var bytes) || bytes.Length == 0)
            return false;

        try
        {
            decoded = ReturnDecoder.DecodeDefaultData(bytes, outputs);
            return true;
        }
        catch (Exception)
        {
            // A revert payload or a non-conforming token returns bytes that do not
            // decode. Never guess a value — the caller reports the field unknown.
            return false;
        }
    }

    /// <summary>Decodes a single <c>uint256</c> return value.</summary>
    public static bool TryDecodeUInt256(string? returnData, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (!TryDecodeReturn(returnData, new[] { new Parameter("uint256", 1) }, out var decoded) || decoded.Count == 0)
            return false;

        if (decoded[0].Result is BigInteger big)
        {
            value = big;
            return true;
        }

        return false;
    }

    /// <summary>Decodes a single <c>uint8</c> return value (ERC-20 <c>decimals()</c>).</summary>
    public static bool TryDecodeUInt8(string? returnData, out int value)
    {
        value = 0;
        if (!TryDecodeReturn(returnData, new[] { new Parameter("uint8", 1) }, out var decoded) || decoded.Count == 0)
            return false;

        switch (decoded[0].Result)
        {
            case BigInteger big when big >= 0 && big <= 255:
                value = (int)big;
                return true;
            case byte b:
                value = b;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Decodes an ERC-20 text field. Tries the standard <c>string</c> encoding first and
    /// falls back to the pre-standard <c>bytes32</c> form (MKR and friends) rather than
    /// reporting a real name as missing.
    /// </summary>
    public static bool TryDecodeText(string? returnData, out string value)
    {
        value = string.Empty;

        if (TryDecodeReturn(returnData, new[] { new Parameter("string", 1) }, out var asString)
            && asString.Count > 0
            && asString[0].Result is string text
            && text.Length > 0)
        {
            value = text;
            return true;
        }

        if (TryDecodeReturn(returnData, new[] { new Parameter("bytes32", 1) }, out var asBytes32)
            && asBytes32.Count > 0
            && asBytes32[0].Result is byte[] raw)
        {
            var trimmed = System.Text.Encoding.UTF8.GetString(raw).TrimEnd('\0').Trim();
            if (trimmed.Length > 0)
            {
                value = trimmed;
                return true;
            }
        }

        return false;
    }

    // ─── Signature parsing ───

    /// <summary>
    /// Parses <c>name(type,type)</c> into its canonical form plus ordered parameters.
    /// Returns null for anything this encoder will not encode exactly — notably tuples,
    /// which need component types the caller never supplies.
    /// </summary>
    public static AbiFunctionSignature? ParseSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var text = signature.Trim();
        var open = text.IndexOf('(');
        if (open <= 0 || !text.EndsWith(")", StringComparison.Ordinal))
            return null;

        var name = text[..open].Trim();
        if (name.Length == 0 || !IsValidIdentifier(name))
            return null;

        var body = text[(open + 1)..^1].Trim();
        if (body.Contains('(', StringComparison.Ordinal) || body.Contains(')', StringComparison.Ordinal))
            return null; // tuple / nested types are not encodable from an untyped argument bag

        var parameters = new List<Parameter>();
        var types = new List<string>();
        if (body.Length > 0)
        {
            var order = 1;
            foreach (var part in body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                // Accept "uint256" and "uint256 amount"; the declared name is discarded
                // because the canonical signature the selector hashes carries types only.
                var type = part.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
                if (!IsSupportedType(type))
                    return null;

                types.Add(type);
                parameters.Add(new Parameter(type, order));
                order++;
            }
        }

        return new AbiFunctionSignature($"{name}({string.Join(',', types)})", name, parameters.ToArray());
    }

    private static bool IsValidIdentifier(string name) =>
        (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static bool IsSupportedType(string type)
    {
        if (type.Length == 0)
            return false;

        var elementType = type.EndsWith("[]", StringComparison.Ordinal) ? type[..^2] : type;
        if (elementType is "address" or "bool" or "string" or "bytes")
            return true;
        if (elementType.StartsWith("bytes", StringComparison.Ordinal)
            && int.TryParse(elementType[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var width))
            return width is >= 1 and <= 32;
        if (TryReadIntegerWidth(elementType, out _, out _))
            return true;

        return false;
    }

    private static bool TryReadIntegerWidth(string type, out bool isSigned, out int bits)
    {
        isSigned = false;
        bits = 0;

        string digits;
        if (type.StartsWith("uint", StringComparison.Ordinal))
        {
            digits = type[4..];
        }
        else if (type.StartsWith("int", StringComparison.Ordinal))
        {
            isSigned = true;
            digits = type[3..];
        }
        else
        {
            return false;
        }

        if (digits.Length == 0)
        {
            bits = 256; // bare `uint` / `int` are aliases for the 256-bit forms
            return true;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out bits))
            return false;

        return bits % 8 == 0 && bits is >= 8 and <= 256;
    }

    // ─── Argument coercion ───

    /// <summary>
    /// Converts an untyped caller argument to the exact CLR value Nethereum's encoder
    /// expects for <paramref name="abiType"/>. Every conversion is exact: binary
    /// floating point is REFUSED outright (a double cannot hold a uint256 without
    /// silently rounding), and out-of-range integers are refused rather than wrapped.
    /// </summary>
    public static bool TryCoerceArgument(string abiType, object? value, out object coerced, out string error)
    {
        coerced = null!;
        error = string.Empty;

        if (value is JsonElement element)
            value = FromJson(element);

        if (value is null)
        {
            error = $"Argument for '{abiType}' is null.";
            return false;
        }

        if (value is float or double)
        {
            error = $"Argument for '{abiType}' arrived as binary floating point, which cannot represent "
                + "Solidity integers exactly. Pass the value as a decimal string or an integer.";
            return false;
        }

        if (abiType.EndsWith("[]", StringComparison.Ordinal))
        {
            var elementType = abiType[..^2];
            if (value is not System.Collections.IEnumerable sequence || value is string)
            {
                error = $"Argument for '{abiType}' must be a sequence.";
                return false;
            }

            var items = new List<object>();
            foreach (var item in sequence)
            {
                if (!TryCoerceArgument(elementType, item, out var coercedItem, out error))
                    return false;
                items.Add(coercedItem);
            }

            coerced = items.ToArray();
            return true;
        }

        if (abiType == "address")
        {
            if (value is string addressText && EthereumAddress.TryParse(addressText, out var checksummed))
            {
                coerced = checksummed;
                return true;
            }

            error = $"Argument for 'address' is not a valid Ethereum address"
                + (value is string ? " (or failed its EIP-55 checksum)." : ".");
            return false;
        }

        if (abiType == "bool")
        {
            switch (value)
            {
                case bool flag:
                    coerced = flag;
                    return true;
                case string boolText when bool.TryParse(boolText, out var parsedFlag):
                    coerced = parsedFlag;
                    return true;
                default:
                    error = "Argument for 'bool' must be a boolean.";
                    return false;
            }
        }

        if (abiType == "string")
        {
            if (value is string stringValue)
            {
                coerced = stringValue;
                return true;
            }

            error = "Argument for 'string' must be a string.";
            return false;
        }

        if (abiType == "bytes" || abiType.StartsWith("bytes", StringComparison.Ordinal))
        {
            byte[]? bytes = value switch
            {
                byte[] raw => raw,
                string hexText when EthereumHex.TryParseData(hexText, out var parsed) => parsed,
                _ => null,
            };

            if (bytes is null)
            {
                error = $"Argument for '{abiType}' must be a byte array or a 0x-prefixed hex string.";
                return false;
            }

            if (abiType.Length > 5
                && int.TryParse(abiType[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var fixedWidth)
                && bytes.Length != fixedWidth)
            {
                error = $"Argument for '{abiType}' must be exactly {fixedWidth} bytes but was {bytes.Length}.";
                return false;
            }

            coerced = bytes;
            return true;
        }

        if (TryReadIntegerWidth(abiType, out var isSigned, out var bits))
        {
            if (!TryToBigInteger(value, out var integer))
            {
                error = $"Argument for '{abiType}' is not an exact integer.";
                return false;
            }

            var (min, max) = IntegerRange(isSigned, bits);
            if (integer < min || integer > max)
            {
                error = $"Argument {integer} does not fit '{abiType}' (range {min}..{max}). "
                    + "Refusing to wrap or truncate.";
                return false;
            }

            coerced = integer;
            return true;
        }

        error = $"Unsupported ABI type '{abiType}'.";
        return false;
    }

    private static (BigInteger Min, BigInteger Max) IntegerRange(bool isSigned, int bits) =>
        isSigned
            ? (-BigInteger.Pow(2, bits - 1), BigInteger.Pow(2, bits - 1) - 1)
            : (BigInteger.Zero, BigInteger.Pow(2, bits) - 1);

    private static bool TryToBigInteger(object value, out BigInteger integer)
    {
        switch (value)
        {
            case BigInteger big:
                integer = big;
                return true;
            case byte or sbyte or short or ushort or int or uint or long:
                integer = new BigInteger(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return true;
            case ulong unsignedLong:
                integer = new BigInteger(unsignedLong);
                return true;
            case decimal money when decimal.Truncate(money) == money:
                integer = new BigInteger(money);
                return true;
            case string text:
                var trimmed = text.Trim();
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return EthereumHex.TryParseQuantity(trimmed, out integer);
                return BigInteger.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer);
            default:
                integer = BigInteger.Zero;
                return false;
        }
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        // The raw token text keeps a uint256 exact; GetDouble would round it.
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(item => (object?)FromJson(item)).ToArray(),
        _ => null,
    };
}

/// <summary>A parsed Solidity function signature: its canonical text, name, and ordered parameters.</summary>
public sealed class AbiFunctionSignature
{
    internal AbiFunctionSignature(string canonical, string name, Parameter[] parameters)
    {
        Canonical = canonical;
        Name = name;
        Parameters = parameters;
    }

    /// <summary>Canonical <c>name(type,type)</c> form — the exact text the selector hashes.</summary>
    public string Canonical { get; }

    /// <summary>Function name without the parameter list.</summary>
    public string Name { get; }

    /// <summary>Ordered input parameters.</summary>
    public Parameter[] Parameters { get; }
}
