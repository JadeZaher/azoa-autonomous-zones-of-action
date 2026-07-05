# Validation & Mapping Layer — Specification

## Goal
Eliminate manual validation and field-by-field mapping boilerplate across controllers and managers by introducing **FluentValidation** (input validation) and **AutoMapper** (entity-DTO mapping) as cross-cutting infrastructure.

## Motivation
Current pain points:
- **Controllers pass raw models directly to managers** with no input validation beyond `[ApiController]` model binding.
- **Managers contain verbose null-check mapping** (e.g. `if (model.Username != null) avatar.Username = model.Username`).
- **No centralized validation rules** — business constraints (email format, password strength, required fields) are implicit or scattered.
- **Mapping logic is duplicated** in every manager method.

## Architecture
```
HTTP Request → FluentValidation pipeline → AutoMapper (RequestModel → Entity) → Manager → Provider
                                               ↑
                                         Validation Rules (single source of truth)
```

### New / Modified Files
| Layer | File | Action |
|---|---|---|
| Project | `AZOA.WebAPI.csproj` | Add `FluentValidation.AspNetCore`, `AutoMapper.Extensions.Microsoft.DependencyInjection` |
| Program | `Program.cs` | Register `FluentValidation`, `AutoMapper` |
| Validation | `Validation/AvatarRegisterValidator.cs` | New |
| Validation | `Validation/AvatarLoginValidator.cs` | New |
| Validation | `Validation/AvatarUpdateValidator.cs` | New |
| Validation | `Validation/HolonCreateValidator.cs` | New |
| Validation | `Validation/HolonUpdateValidator.cs` | New |
| Validation | `Validation/WalletCreateValidator.cs` | New |
| Validation | `Validation/WalletUpdateValidator.cs` | New |
| Validation | `Validation/NftMintValidator.cs` | New |
| Validation | `Validation/SearchRequestValidator.cs` | New |
| Mapping | `Mapping/AZOAMappingProfile.cs` | New |
| Mapping | `Mapping/MappingExtensions.cs` | New (optional lightweight helpers) |

## Validation Layer (FluentValidation)

### Package
```xml
<PackageReference Include="FluentValidation.AspNetCore" Version="11.3.0" />
```

### Registration
```csharp
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
```

### Validator Specifications

**AvatarRegisterValidator**
```csharp
public class AvatarRegisterValidator : AbstractValidator<AvatarRegisterModel>
{
    public AvatarRegisterValidator()
    {
        RuleFor(x => x.Username).NotEmpty().Length(3, 50).Matches("^[a-zA-Z0-9_]+$");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8)
            .Matches("[A-Z]").WithMessage("Password must contain an uppercase letter.")
            .Matches("[a-z]").WithMessage("Password must contain a lowercase letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
        RuleFor(x => x.FirstName).MaximumLength(100);
        RuleFor(x => x.LastName).MaximumLength(100);
    }
}
```

**AvatarLoginValidator**
```csharp
public class AvatarLoginValidator : AbstractValidator<AvatarLoginModel>
{
    public AvatarLoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}
```

**AvatarUpdateValidator**
```csharp
public class AvatarUpdateValidator : AbstractValidator<AvatarUpdateModel>
{
    public AvatarUpdateValidator()
    {
        RuleFor(x => x.Username).Length(3, 50).Matches("^[a-zA-Z0-9_]+$").When(x => x.Username != null);
        RuleFor(x => x.Email).EmailAddress().When(x => x.Email != null);
        RuleFor(x => x.FirstName).MaximumLength(100).When(x => x.FirstName != null);
        RuleFor(x => x.LastName).MaximumLength(100).When(x => x.LastName != null);
    }
}
```

**HolonCreateValidator**
```csharp
public class HolonCreateValidator : AbstractValidator<HolonCreateModel>
{
    public HolonCreateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.ProviderName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Metadata).NotNull();
    }
}
```

**WalletCreateValidator**
```csharp
public class WalletCreateValidator : AbstractValidator<WalletCreateModel>
{
    public WalletCreateValidator()
    {
        RuleFor(x => x.ChainType).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Address).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Label).MaximumLength(200);
    }
}
```

**SearchRequestValidator**
```csharp
public class SearchRequestValidator : AbstractValidator<SearchRequest>
{
    public SearchRequestValidator()
    {
        RuleFor(x => x.Query).MaximumLength(500);
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 100);
        RuleFor(x => x.SortBy).Must(x => new[] { "CreatedDate", "Name", "Relevance" }.Contains(x));
    }
}
```

### Validation Behavior
- FluentValidation runs **before** the controller action when `AddFluentValidationAutoValidation()` is registered.
- Validation failures return **400 Bad Request** with a standardized problem-details style response:
  ```json
  {
    "errors": {
      "Email": ["'Email' must not be empty.", "'Email' is not a valid email address."]
    }
  }
  ```
- Controllers no longer need manual null checks for required fields.

## Mapping Layer (AutoMapper)

### Package
```xml
<PackageReference Include="AutoMapper.Extensions.Microsoft.DependencyInjection" Version="12.0.1" />
```

### Registration
```csharp
builder.Services.AddAutoMapper(typeof(Program).Assembly);
```

### Mapping Profile
```csharp
public class AZOAMappingProfile : Profile
{
    public AZOAMappingProfile()
    {
        // Avatar mappings
        CreateMap<AvatarRegisterModel, Avatar>();
        CreateMap<AvatarUpdateModel, Avatar>()
            .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));

        // Holon mappings
        CreateMap<HolonCreateModel, Holon>();
        CreateMap<HolonUpdateModel, Holon>()
            .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));

        // Wallet mappings
        CreateMap<WalletCreateModel, Wallet>();
        CreateMap<WalletUpdateModel, Wallet>()
            .ForAllMembers(opts => opts.Condition((src, dest, srcMember) => srcMember != null));

        // NFT / Search hit mappings
        CreateMap<IHolon, NftResult>()
            .ForMember(dest => dest.OwnerAvatarId, opt => opt.MapFrom(src => src.AvatarId));
        CreateMap<IHolon, SearchHit>()
            .ForMember(dest => dest.EntityType, opt => opt.MapFrom(_ => SearchableEntityType.Holon))
            .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Name));

        CreateMap<IAvatar, SearchHit>()
            .ForMember(dest => dest.EntityType, opt => opt.MapFrom(_ => SearchableEntityType.Avatar))
            .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Username));

        CreateMap<IWallet, SearchHit>()
            .ForMember(dest => dest.EntityType, opt => opt.MapFrom(_ => SearchableEntityType.Wallet))
            .ForMember(dest => dest.Title, opt => opt.MapFrom(src => src.Address));
    }
}
```

### Mapping in Managers
Replace manual mapping with AutoMapper:

**Before (AvatarManager)**
```csharp
var avatar = existing.Result;
if (model.Username != null) avatar.Username = model.Username;
if (model.Email != null) avatar.Email = model.Email;
// ... 10 more lines
```

**After (AvatarManager)**
```csharp
_mapper.Map(model, existing.Result);  // Null-conditional mapping handled in profile
```

### Benefits
1. **Single source of truth** for validation rules
2. **Consistent error responses** across all endpoints
3. **Reduced manager boilerplate** (20-30 lines per method → 1 line)
4. **Testable mapping logic** via isolated profile tests
5. **Extensible** — new entity types get validation + mapping "for free" by following conventions

## Rollout Plan
1. Add packages and register in `Program.cs`
2. Create `AZOAMappingProfile` with existing entity mappings
3. Create validators for **existing** request models first (Avatar, Holon)
4. Refactor existing managers to use `_mapper.Map`
5. Ensure all existing tests still pass
6. Add validators for new models as Wallet/NFT/Search tracks are implemented

## Acceptance Criteria
- [ ] `FluentValidation.AspNetCore` and `AutoMapper` packages added
- [ ] `Program.cs` registers both services
- [ ] Validators exist for all current request models (AvatarRegister, AvatarLogin, AvatarUpdate, HolonCreate, HolonUpdate)
- [ ] `AZOAMappingProfile` maps all existing entity ↔ model pairs
- [ ] At least one manager refactored to use AutoMapper (prove pattern)
- [ ] Invalid requests return 400 with structured error messages
- [ ] All existing tests pass (no regressions)
- [ ] New validation + mapping unit tests added
- [ ] Stryker mutation score for new code ≥ 50 %
