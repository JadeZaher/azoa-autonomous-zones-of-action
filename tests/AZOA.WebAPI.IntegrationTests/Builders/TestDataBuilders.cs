using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Kyb;
using AZOA.WebAPI.Models.Requests;
using KybVerification = AZOA.WebAPI.Persistence.SurrealDb.Models.KybVerification;
using AZOA.WebAPI.Providers.Kyb;
using Microsoft.Extensions.DependencyInjection;


namespace AZOA.WebAPI.IntegrationTests.Builders;

// ═══════════════════════════════════════════════════════════════════
// Fluent Builder Pattern for test data construction.
// Enables readable, composable test setup:
//   var user = new UserBuilder().WithUsername("neo").Build();
// ═══════════════════════════════════════════════════════════════════

public class UserBuilder
{
    private Guid _id = Guid.NewGuid();
    // Username + email default to UNIQUE values per builder instance. Integration
    // test classes share ONE SurrealDB namespace per class (the app's namespace is
    // pinned per-factory), so methods run serially against the same data store.
    // Constant "testuser"/"test@azoa.local" defaults caused the second seed in a
    // class to fail with "An account with this email already exists." Unique
    // defaults make each SeedUserAsync() independent; tests that need a specific
    // email/username still call WithEmail/WithUsername explicitly.
    private string _username = $"user{Guid.NewGuid():N}"[..16];
    private string _email = $"test_{Guid.NewGuid():N}@azoa.local";
    private string _password = "Password123!";
    private string? _title;
    private string? _firstName;
    private string? _lastName;
    private bool _isActive = true;

    public UserBuilder WithId(Guid id) { _id = id; return this; }
    public UserBuilder WithUsername(string username) { _username = username; return this; }
    public UserBuilder WithEmail(string email) { _email = email; return this; }
    public UserBuilder WithPassword(string password) { _password = password; return this; }
    public UserBuilder WithTitle(string? title) { _title = title; return this; }
    public UserBuilder WithName(string first, string last) { _firstName = first; _lastName = last; return this; }
    public UserBuilder Inactive() { _isActive = false; return this; }

    public User Build()
    {
        return new User
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

    public UserRegisterModel BuildRegisterModel() =>
        new()
        {
            Username = _username,
            Email = _email,
            Password = _password,
            Title = _title,
            FirstName = _firstName,
            LastName = _lastName
        };

    public UserLoginModel BuildLoginModel() =>
        new() { Email = _email, Password = _password };

    public UserUpdateModel BuildUpdateModel() =>
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
    private Guid _userId;
    private string _chainType = "Algorand";
    // Must satisfy WalletCreateModelValidator (^[a-zA-Z0-9]+$): no underscore or
    // hyphen. Guid "N" format is pure hex, so "addr" + N passes the rule.
    private string _address = $"addr{Guid.NewGuid():N}";
    private string? _label;
    private bool _isDefault;

    public WalletBuilder ForUser(Guid userId) { _userId = userId; return this; }
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
            UserId = _userId,
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
    private Guid? _userId;
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
    public HolonBuilder ForUser(Guid userId) { _userId = userId; return this; }
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
            UserId = _userId,
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

public class ActionGraphBundleBuilder
{
    private Guid _id = Guid.NewGuid();
    private string _name = "TestBundle";
    private string _description = "A test bundle";
    private string? _publicKey;
    private Guid? _userId;
    private List<Guid> _boundHolonIds = new();
    private string? _targetChain;
    private string? _generatedCode;
    private string? _deploymentConfig;
    private bool _isActive = true;

    public ActionGraphBundleBuilder WithId(Guid id) { _id = id; return this; }
    public ActionGraphBundleBuilder WithName(string name) { _name = name; return this; }
    public ActionGraphBundleBuilder WithDescription(string description) { _description = description; return this; }
    public ActionGraphBundleBuilder ForUser(Guid userId) { _userId = userId; return this; }
    public ActionGraphBundleBuilder WithPublicKey(string key) { _publicKey = key; return this; }
    public ActionGraphBundleBuilder BoundTo(params Guid[] holonIds) { _boundHolonIds.AddRange(holonIds); return this; }
    public ActionGraphBundleBuilder Targeting(string chain) { _targetChain = chain; return this; }
    public ActionGraphBundleBuilder WithGeneratedCode(string code) { _generatedCode = code; return this; }
    public ActionGraphBundleBuilder WithDeploymentConfig(string config) { _deploymentConfig = config; return this; }
    public ActionGraphBundleBuilder Inactive() { _isActive = false; return this; }

    public ActionGraphBundle Build() =>
        new()
        {
            Id = _id,
            Name = _name,
            Description = _description,
            PublicKey = _publicKey,
            UserId = _userId,
            BoundHolonIds = new List<Guid>(_boundHolonIds),
            TargetChain = _targetChain,
            GeneratedCode = _generatedCode,
            DeploymentConfig = _deploymentConfig,
            IsActive = _isActive
        };

    public ActionGraphBundleCreateModel BuildCreateModel() =>
        new()
        {
            Name = _name,
            Description = _description,
            PublicKey = _publicKey,
            UserId = _userId
        };

    public ActionGraphBundleDappGenerationRequest BuildGenerationRequest() =>
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
    private Guid? _userId;
    private Guid? _walletId;
    private string _operationType = "Mint";
    private string _status = OperationStatus.Pending;
    private Dictionary<string, string> _parameters = new();

    public BlockchainOperationBuilder WithId(Guid id) { _id = id; return this; }
    public BlockchainOperationBuilder ForUser(Guid userId) { _userId = userId; return this; }
    public BlockchainOperationBuilder UsingWallet(Guid walletId) { _walletId = walletId; return this; }
    public BlockchainOperationBuilder OfType(string type) { _operationType = type; return this; }
    public BlockchainOperationBuilder WithStatus(string status) { _status = status; return this; }
    public BlockchainOperationBuilder WithParameter(string key, string value) { _parameters[key] = value; return this; }

    public BlockchainOperation Build() =>
        new()
        {
            Id = _id,
            UserId = _userId,
            WalletId = _walletId,
            OperationType = _operationType,
            Status = _status,
            Parameters = new Dictionary<string, string>(_parameters)
        };
}

/// <summary>Entry point for shared and per-lane builders; lanes add <c>TestDataBuilders.&lt;Lane&gt;.cs</c> partials.</summary>
public static partial class TestDataBuilders
{
    /// <summary>A tenant whose business verification is APPROVED and fresh under the configured KYB authority.</summary>
    public static KybApprovedTenantBuilder KybApprovedTenant(Guid tenantId) => new(tenantId);
}

/// <summary>Writes an APPROVED <c>kyb_verification</c> row through the real <see cref="IKybVerificationStore"/>.</summary>
public sealed class KybApprovedTenantBuilder(Guid tenantId)
{
    private TimeSpan _validity = TimeSpan.FromDays(365);
    private string _companyName = "Builder Test Company";

    public KybApprovedTenantBuilder ValidFor(TimeSpan validity) { _validity = validity; return this; }
    public KybApprovedTenantBuilder WithCompanyName(string companyName) { _companyName = companyName; return this; }

    /// <summary>The approved row for <paramref name="authorityRevision"/>, reviewed at <paramref name="now"/>.</summary>
    public KybVerification Build(string authorityRevision, DateTimeOffset now)
    {
        var attempt = Guid.NewGuid();
        var evidence = new KybBusinessEvidence(_companyName, $"REG-{tenantId:N}"[..16], "GB",
            [new KybBeneficiaryLink("builder-beneficiary", "PERSON", ["DIRECTOR"], [])]);
        return new KybVerification
        {
            Id = tenantId.ToString("N"),
            AttemptId = attempt.ToString("N"),
            AdmissionKey = $"kyb:{tenantId:N}:{attempt:N}",
            RequestKeyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(attempt.ToByteArray())),
            AuthorityRevision = authorityRevision,
            Status = "APPROVED",
            Version = 1,
            BusinessEvidenceJson = System.Text.Json.JsonSerializer.Serialize(evidence),
            CreatedAt = now,
            ReviewedAt = now,
            ExpiresAt = now.Add(_validity),
        };
    }

    /// <summary>Persists the row via the application's KYB store; the tenant must have no verification yet.</summary>
    public async Task<KybVerification> SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IKybVerificationStore>();
        var authority = scope.ServiceProvider.GetRequiredService<IdentomatKybProvider>().AuthorityRevision;
        if (await store.GetAsync(tenantId, ct) is not null)
            throw new InvalidOperationException($"Tenant {tenantId:N} already has a business verification row.");
        var now = scope.ServiceProvider.GetService<TimeProvider>()?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        return await store.TryStartAsync(null, Build(authority, now), ct)
            ?? throw new InvalidOperationException($"A concurrent writer created tenant {tenantId:N}'s verification first.");
    }
}
