// SPDX-License-Identifier: UNLICENSED
// final-hardening-cutover F5 — opt-in Holon AssetType/metadata registry.
// Covers the registry manager CRUD + validation decision table, AND the
// HolonManager opt-in enforcement seam (registered valid/invalid vs unregistered free).

using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using HolonType = AZOA.WebAPI.Persistence.SurrealDb.Models.HolonType;

namespace AZOA.WebAPI.Tests.Managers;

public class HolonTypeRegistryTests
{
    private readonly Mock<IHolonTypeRegistryStore> _store = new();
    private readonly HolonTypeRegistryManager _registry;

    public HolonTypeRegistryTests()
    {
        _registry = new HolonTypeRegistryManager(_store.Object);
    }

    private void SetupNotRegistered(string assetType) =>
        _store.Setup(s => s.GetByAssetTypeAsync(assetType, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<HolonType> { IsError = true, Message = "Holon type not registered." });

    private void SetupRegistered(HolonType type) =>
        _store.Setup(s => s.GetByAssetTypeAsync(type.AssetType, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<HolonType> { Result = type });

    // ─── Registration (CRUD) ───────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_PersistsTypeWithRequiredFields()
    {
        SetupNotRegistered("Song");
        HolonType? saved = null;
        _store.Setup(s => s.UpsertAsync(It.IsAny<HolonType>(), It.IsAny<CancellationToken>()))
              .Callback((HolonType t, CancellationToken _) => saved = t)
              .ReturnsAsync((HolonType t, CancellationToken _) => new AZOAResult<HolonType> { Result = t });

        var model = new HolonTypeRegisterModel
        {
            AssetType = "Song",
            Description = "A musical work",
            RequiredMetadataFields = new List<string> { "isrc", "title", "" }, // blank is dropped
        };

        var result = await _registry.RegisterAsync(model);

        result.IsError.Should().BeFalse();
        saved!.AssetType.Should().Be("Song");
        saved.Id.Should().Be("Song");
        saved.IsActive.Should().BeTrue();
        saved.RequiredMetadataFields.Should().BeEquivalentTo(new[] { "isrc", "title" });
    }

    [Fact]
    public async Task RegisterAsync_EmptyAssetType_IsRejected()
    {
        var result = await _registry.RegisterAsync(new HolonTypeRegisterModel { AssetType = "  " });
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RegisterAsync_ReRegister_PreservesOriginalCreatedAt()
    {
        var originalCreated = DateTimeOffset.UtcNow.AddDays(-10);
        SetupRegistered(new HolonType { AssetType = "Song", CreatedAt = originalCreated });
        HolonType? saved = null;
        _store.Setup(s => s.UpsertAsync(It.IsAny<HolonType>(), It.IsAny<CancellationToken>()))
              .Callback((HolonType t, CancellationToken _) => saved = t)
              .ReturnsAsync((HolonType t, CancellationToken _) => new AZOAResult<HolonType> { Result = t });

        await _registry.RegisterAsync(new HolonTypeRegisterModel { AssetType = "Song" });

        saved!.CreatedAt.Should().Be(originalCreated);
        saved.ModifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DeactivateAsync_FlipsIsActiveFalse()
    {
        SetupRegistered(new HolonType { AssetType = "Song", IsActive = true });
        HolonType? saved = null;
        _store.Setup(s => s.UpsertAsync(It.IsAny<HolonType>(), It.IsAny<CancellationToken>()))
              .Callback((HolonType t, CancellationToken _) => saved = t)
              .ReturnsAsync((HolonType t, CancellationToken _) => new AZOAResult<HolonType> { Result = t });

        var result = await _registry.DeactivateAsync("Song");

        result.IsError.Should().BeFalse();
        saved!.IsActive.Should().BeFalse();
    }

    // ─── Validation decision table ─────────────────────────────────────────

    [Fact]
    public async Task Validate_NullOrEmptyAssetType_Allows()
    {
        (await _registry.ValidateAsync(null, null)).IsError.Should().BeFalse();
        (await _registry.ValidateAsync("", null)).IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_UnregisteredType_Allows()
    {
        SetupNotRegistered("Freeform");
        var result = await _registry.ValidateAsync("Freeform", new Dictionary<string, string>());
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RegisteredButInactive_Allows()
    {
        SetupRegistered(new HolonType
        {
            AssetType = "Song",
            IsActive = false,
            RequiredMetadataFields = new List<string> { "isrc" },
        });
        var result = await _registry.ValidateAsync("Song", new Dictionary<string, string>());
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RegisteredNoRequiredFields_Allows()
    {
        SetupRegistered(new HolonType { AssetType = "Song", IsActive = true, RequiredMetadataFields = null });
        var result = await _registry.ValidateAsync("Song", null);
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RegisteredWithSatisfiedFields_Allows()
    {
        SetupRegistered(new HolonType
        {
            AssetType = "Song",
            IsActive = true,
            RequiredMetadataFields = new List<string> { "isrc", "title" },
        });
        var meta = new Dictionary<string, string> { ["isrc"] = "US-XYZ", ["title"] = "Ambient" };
        var result = await _registry.ValidateAsync("Song", meta);
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_RegisteredWithMissingField_Errors()
    {
        SetupRegistered(new HolonType
        {
            AssetType = "Song",
            IsActive = true,
            RequiredMetadataFields = new List<string> { "isrc", "title" },
        });
        var meta = new Dictionary<string, string> { ["isrc"] = "US-XYZ" }; // title missing
        var result = await _registry.ValidateAsync("Song", meta);
        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("title");
    }

    [Fact]
    public async Task Validate_RegisteredWithEmptyFieldValue_Errors()
    {
        SetupRegistered(new HolonType
        {
            AssetType = "Song",
            IsActive = true,
            RequiredMetadataFields = new List<string> { "isrc" },
        });
        var meta = new Dictionary<string, string> { ["isrc"] = "   " }; // present but blank
        var result = await _registry.ValidateAsync("Song", meta);
        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_RegistryReadFailure_FailsOpen()
    {
        _store.Setup(s => s.GetByAssetTypeAsync("Song", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<HolonType>().CaptureException(new Exception("db down")));
        var result = await _registry.ValidateAsync("Song", new Dictionary<string, string>());
        result.IsError.Should().BeFalse(); // additive constraint, not a security gate
    }

    // ─── HolonManager opt-in enforcement seam ──────────────────────────────

    private static (HolonManager mgr, Mock<IHolonStore> store) BuildHolonManager(IHolonTypeRegistryManager registry)
    {
        var store = new Mock<IHolonStore>();
        store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });
        store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((Guid id, CancellationToken _) =>
                 new AZOAResult<IHolon> { Result = new Holon { Id = id, AssetType = "Song" } });
        return (new HolonManager(store.Object, registry), store);
    }

    [Fact]
    public async Task HolonCreate_RegisteredType_ValidMetadata_Succeeds()
    {
        SetupRegistered(new HolonType { AssetType = "Song", IsActive = true, RequiredMetadataFields = new List<string> { "isrc" } });
        var (mgr, _) = BuildHolonManager(_registry);

        var model = new HolonCreateModel
        {
            Name = "Track", ProviderName = "InMemory", AssetType = "Song",
            Metadata = new Dictionary<string, string> { ["isrc"] = "US-XYZ" },
        };

        var result = await mgr.CreateAsync(model, Guid.NewGuid());
        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task HolonCreate_RegisteredType_MissingMetadata_IsRejected()
    {
        SetupRegistered(new HolonType { AssetType = "Song", IsActive = true, RequiredMetadataFields = new List<string> { "isrc" } });
        var (mgr, store) = BuildHolonManager(_registry);

        var model = new HolonCreateModel
        {
            Name = "Track", ProviderName = "InMemory", AssetType = "Song",
            Metadata = new Dictionary<string, string>(), // isrc missing
        };

        var result = await mgr.CreateAsync(model, Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("isrc");
        store.Verify(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HolonCreate_UnregisteredType_PassesFreely()
    {
        SetupNotRegistered("Whatever");
        var (mgr, store) = BuildHolonManager(_registry);

        var model = new HolonCreateModel
        {
            Name = "Anything", ProviderName = "InMemory", AssetType = "Whatever",
            Metadata = new Dictionary<string, string>(),
        };

        var result = await mgr.CreateAsync(model, Guid.NewGuid());

        result.IsError.Should().BeFalse();
        store.Verify(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HolonCreate_NoRegistryWired_PassesFreely()
    {
        // Null registry (legacy construction) ⇒ validation skipped entirely.
        var store = new Mock<IHolonStore>();
        store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });
        var mgr = new HolonManager(store.Object);

        var model = new HolonCreateModel { Name = "X", ProviderName = "InMemory", AssetType = "Song" };
        var result = await mgr.CreateAsync(model, Guid.NewGuid());

        result.IsError.Should().BeFalse();
    }
}
