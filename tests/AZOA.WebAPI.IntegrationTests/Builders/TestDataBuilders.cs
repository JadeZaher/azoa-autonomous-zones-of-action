using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.IntegrationTests.Builders;

// ═══════════════════════════════════════════════════════════════════
// Fluent Builder Pattern for test data construction.
// Enables readable, composable test setup:
//   var avatar = new AvatarBuilder().WithUsername("neo").Build();
// ═══════════════════════════════════════════════════════════════════

public class AvatarBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _username = "testuser";
    private string _email = "test@azoa.local";
    private string _password = "Password123!";
    private string? _title;
    private string? _firstName;
    private string? _lastName;
    private bool _isActive = true;

    public AvatarBuilder WithId(Guid id) { _id = id; return this; }
    public AvatarBuilder WithUsername(string username) { _username = username; return this; }
    public AvatarBuilder WithEmail(string email) { _email = email; return this; }
    public AvatarBuilder WithPassword(string password) { _password = password; return this; }
    public AvatarBuilder WithTitle(string? title) { _title = title; return this; }
    public AvatarBuilder WithName(string first, string last) { _firstName = first; _lastName = last; return this; }
    public AvatarBuilder Inactive() { _isActive = false; return this; }

    public Avatar Build()
    {
        return new Avatar
        {
            Id = _id,
            Username = _username,
            Email = _email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(_password),
            Title = _title,
            FirstName = _firstName,
            LastName = _lastName,
            IsActive = _isActive
        };
    }

    public AvatarRegisterModel BuildRegisterModel() =>
        new()
        {
            Username = _username,
            Email = _email,
            Password = _password,
            Title = _title,
            FirstName = _firstName,
            LastName = _lastName
        };

    public AvatarLoginModel BuildLoginModel() =>
        new() { Email = _email, Password = _password };

    public AvatarUpdateModel BuildUpdateModel() =>
        new()
        {
            Username = _username,
            Email = _email,
            Title = _title,
            FirstName = _firstName,
            LastName = _lastName,
            IsActive = _isActive
        };
}

public class WalletBuilder
{
    private Guid _id = Guid.NewGuid();
    private Guid _avatarId;
    private string _chainType = "Algorand";
    private string _address = $"addr_{Guid.NewGuid():N}";
    private string? _label;
    private bool _isDefault;

    public WalletBuilder ForAvatar(Guid avatarId) { _avatarId = avatarId; return this; }
    public WalletBuilder OnChain(string chainType) { _chainType = chainType; return this; }
    public WalletBuilder WithAddress(string address) { _address = address; return this; }
    public WalletBuilder WithLabel(string label) { _label = label; return this; }
    public WalletBuilder AsDefault() { _isDefault = true; return this; }

    // Property accessors used by the SurrealDB harness seed helpers.
    public string GetChainType() => _chainType;
    public string GetAddress() => _address;
    public string? GetLabel() => _label;
    public bool GetIsDefault() => _isDefault;

    public Wallet Build() =>
        new()
        {
            Id = _id,
            AvatarId = _avatarId,
            ChainType = _chainType,
            Address = _address,
            Label = _label,
            IsDefault = _isDefault
        };
}

public class HolonBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _name = "TestHolon";
    private string _description = "A test holon";
    private Guid? _avatarId;
    private Guid? _parentHolonId;
    private string _providerName = "InMemory";
    private string? _chainId;
    private string? _assetType;
    private Dictionary<string, string> _metadata = new();
    private List<Guid> _peerHolonIds = new();
    private bool _isActive = true;

    public HolonBuilder WithId(Guid id) { _id = id; return this; }
    public HolonBuilder WithName(string name) { _name = name; return this; }
    public HolonBuilder WithDescription(string description) { _description = description; return this; }
    public HolonBuilder ForAvatar(Guid avatarId) { _avatarId = avatarId; return this; }
    public HolonBuilder OnProvider(string providerName) { _providerName = providerName; return this; }
    public HolonBuilder OnChain(string chainId) { _chainId = chainId; return this; }
    public HolonBuilder AsAsset(string assetType) { _assetType = assetType; return this; }
    public HolonBuilder WithMetadata(string key, string value) { _metadata[key] = value; return this; }
    public HolonBuilder WithPeers(params Guid[] peers) { _peerHolonIds.AddRange(peers); return this; }
    public HolonBuilder WithParent(Guid parentId) { _parentHolonId = parentId; return this; }
    public HolonBuilder Inactive() { _isActive = false; return this; }

    public Holon Build() =>
        new()
        {
            Id = _id,
            Name = _name,
            Description = _description,
            AvatarId = _avatarId,
            ParentHolonId = _parentHolonId,
            ProviderName = _providerName,
            ChainId = _chainId,
            AssetType = _assetType,
            Metadata = new Dictionary<string, string>(_metadata),
            PeerHolonIds = new List<Guid>(_peerHolonIds),
            IsActive = _isActive
        };

    public HolonCreateModel BuildCreateModel() =>
        new()
        {
            Name = _name,
            Description = _description,
            ParentHolonId = _parentHolonId,
            ProviderName = _providerName,
            ChainId = _chainId,
            AssetType = _assetType,
            Metadata = new Dictionary<string, string>(_metadata),
            PeerHolonIds = new List<Guid>(_peerHolonIds)
        };

    public HolonUpdateModel BuildUpdateModel() =>
        new()
        {
            Name = _name,
            Description = _description,
            ProviderName = _providerName,
            ChainId = _chainId,
            AssetType = _assetType,
            Metadata = new Dictionary<string, string>(_metadata),
            PeerHolonIds = new List<Guid>(_peerHolonIds),
            IsActive = _isActive
        };
}

public class STARODKBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _name = "TestODK";
    private string _description = "A test ODK";
    private string? _publicKey;
    private Guid? _avatarId;
    private List<Guid> _boundHolonIds = new();
    private string? _targetChain;
    private string? _generatedCode;
    private string? _deploymentConfig;
    private bool _isActive = true;

    public STARODKBuilder WithId(Guid id) { _id = id; return this; }
    public STARODKBuilder WithName(string name) { _name = name; return this; }
    public STARODKBuilder WithDescription(string description) { _description = description; return this; }
    public STARODKBuilder ForAvatar(Guid avatarId) { _avatarId = avatarId; return this; }
    public STARODKBuilder WithPublicKey(string key) { _publicKey = key; return this; }
    public STARODKBuilder BoundTo(params Guid[] holonIds) { _boundHolonIds.AddRange(holonIds); return this; }
    public STARODKBuilder Targeting(string chain) { _targetChain = chain; return this; }
    public STARODKBuilder WithGeneratedCode(string code) { _generatedCode = code; return this; }
    public STARODKBuilder WithDeploymentConfig(string config) { _deploymentConfig = config; return this; }
    public STARODKBuilder Inactive() { _isActive = false; return this; }

    public STARODK Build() =>
        new()
        {
            Id = _id,
            Name = _name,
            Description = _description,
            PublicKey = _publicKey,
            AvatarId = _avatarId,
            BoundHolonIds = new List<Guid>(_boundHolonIds),
            TargetChain = _targetChain,
            GeneratedCode = _generatedCode,
            DeploymentConfig = _deploymentConfig,
            IsActive = _isActive
        };

    public STARODKCreateModel BuildCreateModel() =>
        new()
        {
            Name = _name,
            Description = _description,
            PublicKey = _publicKey,
            AvatarId = _avatarId
        };

    public STARDappGenerationRequest BuildGenerationRequest() =>
        new()
        {
            TargetChain = _targetChain ?? "Algorand",
            BoundHolonIds = new List<Guid>(_boundHolonIds),
            Config = new Dictionary<string, string> { ["theme"] = "dark" }
        };
}

public class BlockchainOperationBuilder
{
    private Guid _id = Guid.NewGuid();
    private Guid? _avatarId;
    private Guid? _walletId;
    private string _operationType = "Mint";
    private string _status = OperationStatus.Pending;
    private Dictionary<string, string> _parameters = new();

    public BlockchainOperationBuilder WithId(Guid id) { _id = id; return this; }
    public BlockchainOperationBuilder ForAvatar(Guid avatarId) { _avatarId = avatarId; return this; }
    public BlockchainOperationBuilder UsingWallet(Guid walletId) { _walletId = walletId; return this; }
    public BlockchainOperationBuilder OfType(string type) { _operationType = type; return this; }
    public BlockchainOperationBuilder WithStatus(string status) { _status = status; return this; }
    public BlockchainOperationBuilder WithParameter(string key, string value) { _parameters[key] = value; return this; }

    public BlockchainOperation Build() =>
        new()
        {
            Id = _id,
            AvatarId = _avatarId,
            WalletId = _walletId,
            OperationType = _operationType,
            Status = _status,
            Parameters = new Dictionary<string, string>(_parameters)
        };
}
