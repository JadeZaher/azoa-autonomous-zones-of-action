using FluentAssertions;
using FluentValidation.TestHelper;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Validators;
using FV = AZOA.WebAPI.Validators;

namespace AZOA.WebAPI.Tests.Validation;

public class UserRegisterValidatorTests
{
    private readonly UserRegisterValidator _validator = new();

    [Fact]
    public void ValidModel_ShouldNotHaveErrors()
    {
        var model = new UserRegisterModel { Username = "validuser", Email = "test@example.com", Password = "Password123" };
        var result = _validator.TestValidate(model);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyUsername_ShouldHaveError()
    {
        var model = new UserRegisterModel { Username = "", Email = "test@example.com", Password = "Password123" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Username);
    }

    [Fact]
    public void ShortUsername_ShouldHaveError()
    {
        var model = new UserRegisterModel { Username = "ab", Email = "test@example.com", Password = "Password123" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Username);
    }

    [Fact]
    public void InvalidEmail_ShouldHaveError()
    {
        var model = new UserRegisterModel { Username = "validuser", Email = "not-email", Password = "Password123" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void WeakPassword_ShouldHaveErrors()
    {
        var model = new UserRegisterModel { Username = "validuser", Email = "test@example.com", Password = "weak" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }

    [Fact]
    public void UsernameWithSpecialChars_ShouldHaveError()
    {
        var model = new UserRegisterModel { Username = "user@name!", Email = "test@example.com", Password = "Password123" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Username);
    }
}

public class UserLoginValidatorTests
{
    private readonly UserLoginValidator _validator = new();

    [Fact]
    public void ValidModel_ShouldNotHaveErrors()
    {
        var model = new UserLoginModel { Email = "test@example.com", Password = "pass" };
        var result = _validator.TestValidate(model);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyEmail_ShouldHaveError()
    {
        var model = new UserLoginModel { Email = "", Password = "pass" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Email);
    }

    [Fact]
    public void EmptyPassword_ShouldHaveError()
    {
        var model = new UserLoginModel { Email = "test@example.com", Password = "" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }
}

public class HolonCreateValidatorTests
{
    private readonly HolonCreateValidator _validator = new();

    [Fact]
    public void ValidModel_ShouldNotHaveErrors()
    {
        var model = new HolonCreateModel { Name = "TestHolon", Description = "A test", ProviderName = "Test" };
        var result = _validator.TestValidate(model);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void EmptyName_ShouldHaveError()
    {
        var model = new HolonCreateModel { Name = "", Description = "A test", ProviderName = "Test" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void EmptyDescription_ShouldHaveError()
    {
        var model = new HolonCreateModel { Name = "Test", Description = "", ProviderName = "Test" };
        var result = _validator.TestValidate(model);
        result.ShouldHaveValidationErrorFor(x => x.Description);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  FINANCIAL / SAFETY-RELEVANT VALIDATORS  (AZOA.WebAPI.Validators)
//  These guard money-moving and irreversible operations: transfers,
//  faucet top-ups, NFT mint/transfer/burn, cross-chain bridge, action graphs.
// ═══════════════════════════════════════════════════════════════════════════

public class WalletTopUpRequestValidatorTests
{
    private readonly FV.WalletTopUpRequestValidator _validator = new();

    [Fact]
    public void NullAmount_Passes_FallsBackToConfiguredDefault()
    {
        _validator.TestValidate(new WalletTopUpRequest { Amount = null })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void PositiveBoundedAmount_Passes()
    {
        _validator.TestValidate(new WalletTopUpRequest { Amount = 100m })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ZeroOrNegativeAmount_Fails(decimal amount)
    {
        _validator.TestValidate(new WalletTopUpRequest { Amount = amount })
            .ShouldHaveValidationErrorFor("Amount");
    }

    [Fact]
    public void AmountAboveSafetyLimit_Fails()
    {
        _validator.TestValidate(new WalletTopUpRequest { Amount = 1_000_001m })
            .ShouldHaveValidationErrorFor("Amount");
    }
}

public class WalletTransferRequestValidatorTests
{
    private readonly FV.WalletTransferRequestValidator _validator = new();

    // Realistic Solana base58 destination — passes the permissive,
    // chain-agnostic address charset (HIGH-4).
    private const string SolAddr = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";

    private static WalletTransferRequest Valid() => new()
    {
        SourceWalletId = Guid.NewGuid(),
        DestinationAddress = SolAddr,
        Amount = "10.25",
        TokenId = null
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptySourceWalletId_Fails()
    {
        var m = Valid(); m.SourceWalletId = Guid.Empty;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.SourceWalletId);
    }

    [Theory]
    [InlineData("9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM")] // Solana base58
    [InlineData("MFRGGZDFMZTWQ2LKNNWG23TPOBYXE43UOV3HO6DZPJBHK4DUPJ2HK4DUPI")] // Algorand base32
    public void RealChainDestinationAddresses_Pass(string addr)
    {
        var m = Valid(); m.DestinationAddress = addr;
        _validator.TestValidate(m).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]                                       // empty
    [InlineData("addr with space inside the value")]       // whitespace
    [InlineData("..\\..\\secret\\path\\traversalxxxxx")]   // path traversal (backslash not in charset)
    [InlineData("addr\nwith\ncontrolcharacterssss")]       // control chars
    [InlineData("bad!address$with%symbolsssss")]           // out-of-charset symbols
    [InlineData("tooshort")]                               // below min length
    public void InvalidDestinationAddress_Fails(string addr)
    {
        var m = Valid(); m.DestinationAddress = addr;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.DestinationAddress);
    }

    [Fact]
    public void TooLongDestinationAddress_Fails()
    {
        var m = Valid(); m.DestinationAddress = new string('A', 129);
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.DestinationAddress);
    }

    // Regression: relaxing the address charset must NOT loosen the amount rule.
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("notanumber")]
    public void NonPositiveOrNonNumericAmount_Fails(string amount)
    {
        var m = Valid(); m.Amount = amount;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void InvalidTokenIdChars_Fails()
    {
        var m = Valid(); m.TokenId = "bad token!";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.TokenId);
    }
}

public class WalletCreateModelValidatorTests
{
    private readonly FV.WalletCreateModelValidator _validator = new();

    private static WalletCreateModel Valid() => new()
    {
        ChainType = "Solana",
        Address = "Addr123",
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyChainType_Fails()
    {
        var m = Valid(); m.ChainType = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.ChainType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("addr with space")]
    [InlineData("addr-dash")]
    public void InvalidAddress_Fails(string addr)
    {
        var m = Valid(); m.Address = addr;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Address);
    }

    [Fact]
    public void PublicKeyWithInvalidChars_Fails()
    {
        var m = Valid(); m.PublicKey = "key with space";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.PublicKey);
    }
}

public class NftMintRequestValidatorTests
{
    private readonly FV.NftMintRequestValidator _validator = new();

    private static NftMintRequest Valid() => new()
    {
        WalletId = Guid.NewGuid(),
        Name = "MyNFT",
        Description = "A test NFT",
        ChainId = "solana"
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyWalletId_Fails()
    {
        var m = Valid(); m.WalletId = Guid.Empty;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.WalletId);
    }

    [Fact]
    public void EmptyName_Fails()
    {
        var m = Valid(); m.Name = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void EmptyDescription_Fails()
    {
        var m = Valid(); m.Description = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Description);
    }

    [Fact]
    public void EmptyChainId_Fails()
    {
        var m = Valid(); m.ChainId = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.ChainId);
    }

    [Fact]
    public void InvalidTokenIdChars_Fails()
    {
        var m = Valid(); m.TokenId = "bad/token";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.TokenId);
    }
}

public class NftTransferRequestValidatorTests
{
    private readonly FV.NftTransferRequestValidator _validator = new();

    private static NftTransferRequest Valid() => new()
    {
        TargetUserId = Guid.NewGuid(),
        WalletId = Guid.NewGuid()
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyTargetUserId_Fails()
    {
        var m = Valid(); m.TargetUserId = Guid.Empty;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.TargetUserId);
    }

    [Fact]
    public void EmptyWalletId_Fails()
    {
        var m = Valid(); m.WalletId = Guid.Empty;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.WalletId);
    }

    [Fact]
    public void OverLongMemo_Fails()
    {
        var m = Valid(); m.Memo = new string('x', 513);
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Memo);
    }
}

public class NftBurnRequestValidatorTests
{
    private readonly FV.NftBurnRequestValidator _validator = new();

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(new NftBurnRequest { WalletId = Guid.NewGuid() })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyWalletId_Fails()
    {
        _validator.TestValidate(new NftBurnRequest { WalletId = Guid.Empty })
            .ShouldHaveValidationErrorFor(x => x.WalletId);
    }
}

public class BridgeInitiateRequestValidatorTests
{
    private readonly FV.BridgeInitiateRequestValidator _validator = new();

    // Realistic Solana base58 recipient — passes the permissive,
    // chain-agnostic address charset (HIGH-4).
    private const string SolAddr = "9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM";

    private static BridgeInitiateRequest Valid() => new()
    {
        SourceChain = "solana",
        TargetChain = "algorand",
        TokenId = "token-1",
        RecipientAddress = SolAddr,
        Amount = "5"
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptySourceChain_Fails()
    {
        var m = Valid(); m.SourceChain = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.SourceChain);
    }

    [Fact]
    public void SameSourceAndTargetChain_Fails()
    {
        var m = Valid(); m.SourceChain = "solana"; m.TargetChain = "Solana";
        var result = _validator.Validate(m);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage == "SourceChain and TargetChain must be different.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad token!")]
    public void InvalidTokenId_Fails(string tokenId)
    {
        var m = Valid(); m.TokenId = tokenId;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.TokenId);
    }

    [Theory]
    [InlineData("9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM")] // Solana base58
    [InlineData("MFRGGZDFMZTWQ2LKNNWG23TPOBYXE43UOV3HO6DZPJBHK4DUPJ2HK4DUPI")] // Algorand base32
    public void RealChainRecipientAddresses_Pass(string addr)
    {
        var m = Valid(); m.RecipientAddress = addr;
        _validator.TestValidate(m).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]                                       // empty
    [InlineData("addr with space inside the value")]       // whitespace
    [InlineData("..\\..\\..\\etc\\passwd\\traversalxx")]   // path traversal (backslash not in charset)
    [InlineData("addr\twith\tcontrolcharacterssss")]       // control chars
    [InlineData("bad!address$with%symbolsssss")]           // out-of-charset symbols
    [InlineData("tooshort")]                               // below min length
    public void InvalidRecipientAddress_Fails(string addr)
    {
        var m = Valid(); m.RecipientAddress = addr;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.RecipientAddress);
    }

    [Fact]
    public void TooLongRecipientAddress_Fails()
    {
        var m = Valid(); m.RecipientAddress = new string('A', 129);
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.RecipientAddress);
    }

    // Regression: relaxing the address charset must NOT loosen the amount rule.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonPositiveAmount_Fails(string amount)
    {
        var m = Valid(); m.Amount = amount;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Theory]
    [InlineData("9007199254740993")]
    [InlineData("18446744073709551615")]
    public void PrecisionSafeUlongAmount_Passes(string amount)
    {
        var m = Valid(); m.Amount = amount;
        _validator.TestValidate(m).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("18446744073709551616")]
    [InlineData("1.0")]
    [InlineData("+1")]
    public void AmountOutsideCanonicalUlongRange_Fails(string amount)
    {
        var m = Valid(); m.Amount = amount;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Amount);
    }
}

public class BridgeReverseRequestValidatorTests
{
    private readonly FV.BridgeReverseRequestValidator _validator = new();

    [Theory]
    [InlineData("9WzDXwBbmkg8ZTbNMqUxvQRAyrZzDsGYdLVL9zYtAWWM")] // Solana base58
    [InlineData("MFRGGZDFMZTWQ2LKNNWG23TPOBYXE43UOV3HO6DZPJBHK4DUPJ2HK4DUPI")] // Algorand base32
    public void Valid_RealChainAddresses_Pass(string addr)
    {
        _validator.TestValidate(new BridgeReverseRequest { SourceRecipientAddress = addr })
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]                                       // empty
    [InlineData("addr with space inside the value")]       // whitespace
    [InlineData("..\\..\\..\\etc\\passwd\\traversalxx")]   // path traversal (backslash not in charset)
    [InlineData("addr\twith\tcontrolcharacterssss")]       // control chars
    [InlineData("bad!address$with%symbolsssss")]           // out-of-charset symbols
    [InlineData("tooshort")]                               // below min length
    public void InvalidSourceRecipientAddress_Fails(string addr)
    {
        _validator.TestValidate(new BridgeReverseRequest { SourceRecipientAddress = addr })
            .ShouldHaveValidationErrorFor(x => x.SourceRecipientAddress);
    }

    [Fact]
    public void TooLongSourceRecipientAddress_Fails()
    {
        _validator.TestValidate(new BridgeReverseRequest { SourceRecipientAddress = new string('A', 129) })
            .ShouldHaveValidationErrorFor(x => x.SourceRecipientAddress);
    }
}

public class ActionCreateModelValidatorTests
{
    private readonly FV.ActionCreateModelValidator _validator = new();

    private static ActionCreateModel Valid() => new()
    {
        Name = "My Action",
        Description = "An action",
        Nodes = new List<ActionNodeCreateModel>
        {
            new() { Name = "Start", NodeType = ActionNodeType.HolonCreate, IsEntry = true,  IsTerminal = false },
            new() { Name = "End",   NodeType = ActionNodeType.HolonGet,    IsEntry = false, IsTerminal = true  }
        },
        Edges = new List<ActionEdgeCreateModel>
        {
            new() { SourceNodeId = 0, TargetNodeId = 1, EdgeType = ActionEdgeType.Control }
        }
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyName_Fails()
    {
        var m = Valid(); m.Name = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void NoEntryNode_Fails()
    {
        var m = Valid();
        foreach (var n in m.Nodes) n.IsEntry = false;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Nodes);
    }

    [Fact]
    public void NoTerminalNode_Fails()
    {
        var m = Valid();
        foreach (var n in m.Nodes) n.IsTerminal = false;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Nodes);
    }

    [Fact]
    public void SelfLoopEdge_Fails()
    {
        var m = Valid();
        m.Edges[0].TargetNodeId = m.Edges[0].SourceNodeId;
        // Self-loop is rejected by the child ActionEdgeCreateModelValidator.
        _validator.TestValidate(m).IsValid.Should().BeFalse();
    }
}

public class ActionEdgeCreateModelValidatorTests
{
    private readonly FV.ActionEdgeCreateModelValidator _validator = new();

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(new ActionEdgeCreateModel
        {
            SourceNodeId = 0,
            TargetNodeId = 1,
            EdgeType = ActionEdgeType.Control
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void SelfLoop_Fails()
    {
        var result = _validator.Validate(new ActionEdgeCreateModel
        {
            SourceNodeId = 2,
            TargetNodeId = 2,
            EdgeType = ActionEdgeType.Control
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e =>
            e.ErrorMessage.Contains("self-loops not allowed"));
    }

    [Fact]
    public void NegativeSourceNodeId_Fails()
    {
        _validator.TestValidate(new ActionEdgeCreateModel
        {
            SourceNodeId = -1,
            TargetNodeId = 1,
            EdgeType = ActionEdgeType.Control
        }).ShouldHaveValidationErrorFor(x => x.SourceNodeId);
    }

    [Fact]
    public void InvalidEdgeTypeEnum_Fails()
    {
        _validator.TestValidate(new ActionEdgeCreateModel
        {
            SourceNodeId = 0,
            TargetNodeId = 1,
            EdgeType = (ActionEdgeType)42
        }).ShouldHaveValidationErrorFor(x => x.EdgeType);
    }

    // FR-1b / AC-1b: Conditional edges require non-empty Condition.
    [Fact]
    public void Conditional_EmptyCondition_Fails()
    {
        var result = _validator.Validate(new ActionEdgeCreateModel
        {
            SourceNodeId = 0,
            TargetNodeId = 1,
            EdgeType = ActionEdgeType.Conditional,
            Condition = ""
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Condition is required"));
    }

    [Fact]
    public void Conditional_NullCondition_Fails()
    {
        var result = _validator.Validate(new ActionEdgeCreateModel
        {
            SourceNodeId = 0,
            TargetNodeId = 1,
            EdgeType = ActionEdgeType.Conditional,
            Condition = null
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Condition is required"));
    }

    [Fact]
    public void Conditional_WithCondition_Passes()
    {
        _validator.TestValidate(new ActionEdgeCreateModel
        {
            SourceNodeId = 0,
            TargetNodeId = 1,
            EdgeType = ActionEdgeType.Conditional,
            Condition = "x == 1"
        }).IsValid.Should().BeTrue();
    }
}

public class ActionEdgeAddModelValidatorTests
{
    private readonly FV.ActionEdgeAddModelValidator _validator = new();

    [Fact]
    public void Control_Valid_Passes()
    {
        _validator.TestValidate(new ActionEdgeAddModel
        {
            SourceNodeId = Guid.NewGuid(),
            TargetNodeId = Guid.NewGuid(),
            EdgeType = ActionEdgeType.Control
        }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void SelfLoop_Fails()
    {
        var id = Guid.NewGuid();
        var result = _validator.Validate(new ActionEdgeAddModel
        {
            SourceNodeId = id,
            TargetNodeId = id,
            EdgeType = ActionEdgeType.Control
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("self-loops not allowed"));
    }

    // FR-1b / AC-1b: same Condition rule applies to the post-hoc add surface.
    [Fact]
    public void Conditional_EmptyCondition_Fails()
    {
        var result = _validator.Validate(new ActionEdgeAddModel
        {
            SourceNodeId = Guid.NewGuid(),
            TargetNodeId = Guid.NewGuid(),
            EdgeType = ActionEdgeType.Conditional,
            Condition = ""
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("Condition is required"));
    }

    [Fact]
    public void Conditional_WithCondition_Passes()
    {
        _validator.TestValidate(new ActionEdgeAddModel
        {
            SourceNodeId = Guid.NewGuid(),
            TargetNodeId = Guid.NewGuid(),
            EdgeType = ActionEdgeType.Conditional,
            Condition = "score > 10"
        }).IsValid.Should().BeTrue();
    }
}

// ─── Representative sample of the remaining (non-financial) validators ───

public class WalletConnectRequestValidatorTests
{
    private readonly FV.WalletConnectRequestValidator _validator = new();

    private static WalletConnectRequest Valid() => new()
    {
        ChainType = "Ethereum",
        Address = "Addr123"
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyChainType_Fails()
    {
        var m = Valid(); m.ChainType = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.ChainType);
    }

    [Fact]
    public void InvalidAddress_Fails()
    {
        var m = Valid(); m.Address = "addr-with-dash";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Address);
    }
}

public class HolonCloneRequestValidatorTests
{
    private readonly FV.HolonCloneRequestValidator _validator = new();

    [Fact]
    public void DefaultRequest_Passes()
    {
        _validator.TestValidate(new HolonCloneRequest()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyNameWhenProvided_Fails()
    {
        _validator.TestValidate(new HolonCloneRequest { Name = "" })
            .ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void EmptyNewParentGuid_Fails()
    {
        _validator.TestValidate(new HolonCloneRequest { NewParentId = Guid.Empty })
            .ShouldHaveValidationErrorFor("NewParentId");
    }
}

public class FvSearchRequestValidatorTests
{
    private readonly FV.SearchRequestValidator _validator = new();

    private static SearchRequest Valid() => new()
    {
        Query = "term",
        Page = 1,
        PageSize = 20,
        SortBy = "CreatedDate"
    };

    [Fact]
    public void Valid_Passes()
    {
        _validator.TestValidate(Valid()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void EmptyQuery_Fails()
    {
        var m = Valid(); m.Query = "";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.Query);
    }

    [Fact]
    public void InvalidSortBy_Fails()
    {
        var m = Valid(); m.SortBy = "NotAField";
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.SortBy);
    }

    [Fact]
    public void PageSizeAboveMax_Fails()
    {
        var m = Valid(); m.PageSize = 201;
        _validator.TestValidate(m).ShouldHaveValidationErrorFor(x => x.PageSize);
    }

    [Fact]
    public void CreatedBeforeEarlierThanAfter_Fails()
    {
        var m = Valid();
        m.CreatedAfter = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        m.CreatedBefore = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        _validator.TestValidate(m).ShouldHaveValidationErrorFor("CreatedBefore");
    }
}
