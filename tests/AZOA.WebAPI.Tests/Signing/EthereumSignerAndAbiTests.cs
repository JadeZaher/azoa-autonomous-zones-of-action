using System.Numerics;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;
using Nethereum.Model;
using Nethereum.Signer;
using Xunit;

namespace AZOA.WebAPI.Tests.Signing;

/// <summary>
/// The Ethereum signing primitives, exercised with real secp256k1 keys and no network.
/// Pins the canonical-byte contract (unsigned type-2 payload in, submittable envelope out),
/// the amount type that must carry more than the retired ulong surface could, and the ABI
/// encoder's exactness rules.
/// </summary>
public class EthereumSignerAndAbiTests
{
    private static readonly BigInteger SepoliaChainId = 11155111;
    private static readonly BigInteger TwentyEtherInWei = BigInteger.Parse("20000000000000000000");

    private readonly EthereumTransactionSigner _signer = new();
    private readonly EthECKey _key = EthECKey.GenerateKey();

    private Transaction1559 NewTransaction(BigInteger value) => new(
        SepoliaChainId,
        nonce: 7,
        maxPriorityFeePerGas: 1_500_000_000,
        maxFeePerGas: 30_000_000_000,
        gasLimit: 21_000,
        receiverAddress: "0x5B38Da6a701c568545dCfcB03FcB875f56beddC4",
        amount: value,
        data: "0x",
        accessList: null);

    // ─── Signer ───

    [Fact]
    public void Signer_returns_a_submittable_envelope_that_recovers_to_the_signing_key()
    {
        var transaction = NewTransaction(1_000_000);
        using var material = new SigningKeyMaterial(_key.GetPrivateKeyAsBytes());

        var signed = _signer.Sign(transaction.GetRLPEncodedRaw(), material);

        signed.IsError.Should().BeFalse(signed.Message);
        signed.Result.Should().NotBeNull();

        // The returned bytes must be the wire transaction eth_sendRawTransaction takes:
        // decodable, and recovering to the key that signed it.
        var decoded = Transaction1559Encoder.Current.Decode(signed.Result!);
        EthereumAddress.AreSame(
                TransactionVerificationAndRecovery.GetSenderAddress(decoded), _key.GetPublicAddress())
            .Should().BeTrue();
        TransactionVerificationAndRecovery.VerifyTransaction(decoded).Should().BeTrue();
    }

    [Fact]
    public void Signer_output_matches_nethereums_own_type2_signing_path_byte_for_byte()
    {
        var transaction = NewTransaction(1_000_000);
        using var material = new SigningKeyMaterial(_key.GetPrivateKeyAsBytes());

        var signed = _signer.Sign(transaction.GetRLPEncodedRaw(), material);
        var reference = new Transaction1559Signer().SignTransaction(_key, NewTransaction(1_000_000));
        var referenceHex = (reference.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? reference[2..]
            : reference).ToLowerInvariant();

        signed.IsError.Should().BeFalse(signed.Message);
        Convert.ToHexStringLower(signed.Result!).Should().Be(referenceHex);
    }

    [Fact]
    public void Signer_preserves_an_amount_far_above_the_ulong_interface_ceiling()
    {
        // 20 ETH in wei exceeds ulong.MaxValue. If any part of the pipeline routed the
        // value through a 64-bit integer this would come back wrapped.
        var transaction = NewTransaction(TwentyEtherInWei);
        using var material = new SigningKeyMaterial(_key.GetPrivateKeyAsBytes());

        var signed = _signer.Sign(transaction.GetRLPEncodedRaw(), material);

        signed.IsError.Should().BeFalse(signed.Message);
        Transaction1559Encoder.Current.Decode(signed.Result!).Amount.Should().Be(TwentyEtherInWei);
        TwentyEtherInWei.Should().BeGreaterThan(new BigInteger(ulong.MaxValue),
            "this is the amount the retired ulong interface surface could not express at all");
    }

    [Fact]
    public void Signer_refuses_a_payload_that_is_not_an_eip1559_typed_transaction()
    {
        using var material = new SigningKeyMaterial(_key.GetPrivateKeyAsBytes());

        // A legacy transaction begins with an RLP list header, not the 0x02 type byte.
        var signed = _signer.Sign(new byte[] { 0xf8, 0x6c, 0x01 }, material);

        signed.IsError.Should().BeTrue();
        signed.Message.Should().Contain("EIP-1559 (type 0x02)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(64)]
    public void Signer_refuses_a_wrong_length_key(int keyLength)
    {
        using var material = new SigningKeyMaterial(new byte[keyLength]);

        var signed = _signer.Sign(NewTransaction(1).GetRLPEncodedRaw(), material);

        signed.IsError.Should().BeTrue();
        signed.Message.Should().Contain("32 bytes");
    }

    [Fact]
    public void Signer_refuses_a_thirty_two_byte_key_that_is_not_a_valid_curve_scalar()
    {
        // An all-zero buffer is the right LENGTH but not a point on the curve. The length
        // check alone would have let it through.
        using var material = new SigningKeyMaterial(new byte[32]);

        var signed = _signer.Sign(NewTransaction(1).GetRLPEncodedRaw(), material);

        signed.IsError.Should().BeTrue();
        signed.Message.Should().Contain("secp256k1");
    }

    // ─── Amount type (D13/D15) ───
    // The former EthereumBoundaryAmount widen/narrow edge is deleted; these assert the
    // same invariants against RailAmount, which is what the interface now carries.

    [Fact]
    public void RailAmount_carries_an_amount_the_retired_ulong_surface_could_not_express()
    {
        var amount = RailAmount.OfChainAsset(TwentyEtherInWei);

        amount.BaseUnits.Should().Be(TwentyEtherInWei);
        amount.ToBaseUnitString().Should().Be("20000000000000000000");

        // The wrapped value a silent (ulong) cast would have produced. Nothing in the
        // pipeline may ever hold this number.
        var wrapped = (ulong)(TwentyEtherInWei & ulong.MaxValue);
        wrapped.Should().NotBe(0);
        ((BigInteger)wrapped).Should().NotBe(amount.BaseUnits);
    }

    [Fact]
    public void RailAmount_refuses_to_narrow_above_the_ulong_ceiling_instead_of_truncating()
    {
        // A chain whose SDK is genuinely 64-bit (Algorand ASA, SPL) still has to narrow.
        // It must refuse, never wrap — the old boundary's one non-negotiable property.
        var fits = RailAmount.OfChainAsset(TwentyEtherInWei).TryToUInt64(out var narrowed);

        fits.Should().BeFalse("20 ETH in wei exceeds ulong.MaxValue");
        narrowed.Should().Be(0, "a refused conversion must not hand back a truncated value");
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void RailAmount_round_trips_every_amount_the_old_surface_could_hold(ulong amount)
    {
        RailAmount widened = amount;

        widened.BaseUnits.Should().Be(new BigInteger(amount));
        widened.IsChainDenominated.Should().BeTrue();
        widened.TryToUInt64(out var narrowed).Should().BeTrue();
        narrowed.Should().Be(amount);
    }

    [Fact]
    public void RailAmount_refuses_a_negative_amount_at_construction()
    {
        var construct = () => RailAmount.In(ValueDimension.NativeChainAsset, BigInteger.MinusOne);

        construct.Should().Throw<ArgumentOutOfRangeException>(
            "a reversal is a separate operation, never a signed amount on a transfer");
    }

    [Fact]
    public void RailAmount_refuses_to_narrow_a_fiat_amount_into_chain_base_units()
    {
        // The dimension is the reason the shared amount type is safe to share: fiat cents
        // read as chain base units would move a different quantity of a different thing.
        var fiveDollars = RailAmount.OfUsdMinorUnits(500);

        fiveDollars.IsChainDenominated.Should().BeFalse();
        fiveDollars.TryToUInt64(out var narrowed).Should().BeFalse();
        narrowed.Should().Be(0);
        fiveDollars.Dimension.Should().Be(ValueDimension.Usd);
    }

    // ─── ABI ───

    [Fact]
    public void Abi_encodes_the_canonical_erc20_transfer_call()
    {
        var calldata = EthereumAbi.EncodeCall(
            EthereumAbi.TransferSignature, "0x5B38Da6a701c568545dCfcB03FcB875f56beddC4", TwentyEtherInWei);

        // keccak("transfer(address,uint256)")[0..4] is the published ERC-20 selector.
        calldata.Should().StartWith("0xa9059cbb");
        calldata.Should().HaveLength(2 + 8 + 64 + 64);
        calldata.Should().EndWith(
            "0000000000000000000000005b38da6a701c568545dcfcb03fcb875f56beddc4"
            + "000000000000000000000000000000000000000000000001158e460913d00000");
    }

    [Fact]
    public void Abi_selectors_match_the_published_erc20_values()
    {
        EthereumAbi.Selector(EthereumAbi.BalanceOfSignature).Should().Be("70a08231");
        EthereumAbi.Selector(EthereumAbi.TotalSupplySignature).Should().Be("18160ddd");
        EthereumAbi.Selector(EthereumAbi.MintSignature).Should().Be("40c10f19");
        EthereumAbi.Selector(EthereumAbi.BurnSignature).Should().Be("42966c68");
    }

    [Theory]
    [InlineData(1.5d)]
    [InlineData(1.0f)]
    public void Abi_refuses_a_binary_floating_point_integer_argument(object value)
    {
        EthereumAbi.TryCoerceArgument("uint256", value, out _, out var error).Should().BeFalse();
        error.Should().Contain("binary floating point");
    }

    [Fact]
    public void Abi_refuses_an_out_of_range_integer_rather_than_wrapping_it()
    {
        EthereumAbi.TryCoerceArgument("uint8", 256, out _, out var error).Should().BeFalse();
        error.Should().Contain("Refusing to wrap or truncate");
    }

    [Fact]
    public void Abi_carries_a_uint256_string_argument_exactly()
    {
        EthereumAbi.TryCoerceArgument("uint256", TwentyEtherInWei.ToString(), out var coerced, out _)
            .Should().BeTrue();

        coerced.Should().Be(TwentyEtherInWei);
    }

    [Fact]
    public void Abi_refuses_an_address_argument_with_a_broken_checksum()
    {
        // Last character flipped from the valid EIP-55 form.
        EthereumAbi.TryCoerceArgument("address", "0x5B38Da6a701c568545dCfcB03FcB875f56beddC5", out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("EIP-55");
    }

    [Fact]
    public void Abi_refuses_a_tuple_signature_it_cannot_encode_exactly()
    {
        EthereumAbi.ParseSignature("swap((address,uint256),bool)").Should().BeNull();
    }

    // ─── Address ───

    [Fact]
    public void Address_rejects_a_mixed_case_address_that_fails_its_checksum()
    {
        EthereumAddress.TryParse("0x5B38Da6a701c568545dCfcB03FcB875f56beddC5", out _).Should().BeFalse();
    }

    [Fact]
    public void Address_accepts_a_case_free_address_and_returns_its_checksummed_form()
    {
        EthereumAddress.TryParse("0x5b38da6a701c568545dcfcb03fcb875f56beddc4", out var checksummed)
            .Should().BeTrue("an all-lowercase address carries no checksum information");
        checksummed.Should().Be("0x5B38Da6a701c568545dCfcB03FcB875f56beddC4");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ETH")]
    [InlineData("0x0000000000000000000000000000000000000000")]
    public void Address_treats_the_native_sentinels_as_ether(string? tokenId)
    {
        EthereumAddress.IsNativeToken(tokenId).Should().BeTrue();
    }

    [Fact]
    public void Address_treats_a_contract_address_as_a_token()
    {
        EthereumAddress.IsNativeToken("0x5B38Da6a701c568545dCfcB03FcB875f56beddC4").Should().BeFalse();
    }

    // ─── Hex ───

    [Fact]
    public void Hex_parses_a_quantity_larger_than_any_64_bit_integer()
    {
        EthereumHex.TryParseQuantity("0x1158e460913d00000", out var value).Should().BeTrue();
        value.Should().Be(TwentyEtherInWei);
    }

    [Theory]
    [InlineData("0x")]
    [InlineData("1234")]
    [InlineData("0xzz")]
    [InlineData(null)]
    public void Hex_refuses_a_malformed_quantity(string? hex)
    {
        EthereumHex.TryParseQuantity(hex, out _).Should().BeFalse();
    }

    [Fact]
    public void Hex_emits_minimal_quantities()
    {
        EthereumHex.Quantity(BigInteger.Zero).Should().Be("0x0");
        EthereumHex.Quantity(new BigInteger(255)).Should().Be("0xff");
        EthereumHex.Quantity(TwentyEtherInWei).Should().Be("0x1158e460913d00000");
    }
}
