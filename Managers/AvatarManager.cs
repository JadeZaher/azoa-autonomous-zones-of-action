using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Admin;
using SurrealForge.Client;

namespace AZOA.WebAPI.Managers;

public class AvatarManager : IAvatarManager
{
    private readonly IAvatarStore _avatarStore;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _environment;
    private readonly IAdminBootstrapStateStore? _adminBootstrapStateStore;

    public AvatarManager(
        IAvatarStore avatarStore,
        IConfiguration config,
        IHostEnvironment environment,
        IAdminBootstrapStateStore? adminBootstrapStateStore = null)
    {
        _avatarStore = avatarStore;
        _config = config;
        _environment = environment;
        _adminBootstrapStateStore = adminBootstrapStateStore;
    }

    public async Task<AZOAResult<IAvatar>> RegisterAsync(AvatarRegisterModel model, AZOARequest? request = null)
    {
        // Check for duplicate email
        var allAvatars = await _avatarStore.GetAllAsync(default);
        if (allAvatars.Result?.Any(a => a.Email.Equals(model.Email, StringComparison.OrdinalIgnoreCase)) == true)
            return new AZOAResult<IAvatar> { IsError = true, Message = "An account with this email already exists." };

        // Check for duplicate username
        if (allAvatars.Result?.Any(a => a.Username.Equals(model.Username, StringComparison.OrdinalIgnoreCase)) == true)
            return new AZOAResult<IAvatar> { IsError = true, Message = "This username is already taken." };

        var avatar = new Avatar
        {
            Username = model.Username,
            Email = model.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password),
            Title = model.Title,
            FirstName = model.FirstName,
            LastName = model.LastName
        };

        return await _avatarStore.UpsertAsync(avatar, default);
    }

    public async Task<AZOAResult<string>> LoginAsync(AvatarLoginModel model, AZOARequest? request = null)
    {
        var all = await _avatarStore.GetAllAsync(default);
        var avatar = all.Result?.FirstOrDefault(a => a.Email.Equals(model.Email, StringComparison.OrdinalIgnoreCase));

        if (avatar == null || !BCrypt.Net.BCrypt.Verify(model.Password, avatar.PasswordHash))
            return new AZOAResult<string> { IsError = true, Message = "Invalid credentials." };

        var bootstrap = await ResolveBootstrapAuthorityAsync(avatar, model.BootstrapSecret);
        if (bootstrap.IsError)
            return new AZOAResult<string> { IsError = true, Message = bootstrap.Message };

        var token = GenerateJwt(avatar, bootstrap.Result == true);
        return new AZOAResult<string> { Result = token, Message = "Login successful." };
    }

    public async Task<AZOAResult<IAvatar>> GetAsync(Guid id, AZOARequest? request = null)
    {
        return await _avatarStore.GetByIdAsync(id, default);
    }

    public async Task<AZOAResult<IEnumerable<IAvatar>>> GetAllAsync(AZOARequest? request = null)
    {
        return await _avatarStore.GetAllAsync(default);
    }

    public async Task<AZOAResult<IAvatar>> UpdateAsync(Guid id, AvatarUpdateModel model, Guid avatarId, AZOARequest? request = null)
    {
        // IDOR guard: an avatar may only edit its own record — the route id
        // must match the authenticated avatar identity.
        if (id != avatarId)
            return new AZOAResult<IAvatar> { IsError = true, Message = "You may only update your own avatar." };

        var existing = await _avatarStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;

        var avatar = existing.Result;
        if (model.Email is not null)
        {
            var emailValidation = await ValidateEmailUpdateAsync(avatar, model.Email);
            if (emailValidation.IsError)
                return new AZOAResult<IAvatar> { IsError = true, Message = emailValidation.Message };
        }
        if (model.Username != null) avatar.Username = model.Username;
        if (model.Email != null) avatar.Email = model.Email;
        if (model.Title != null) avatar.Title = model.Title;
        if (model.FirstName != null) avatar.FirstName = model.FirstName;
        if (model.LastName != null) avatar.LastName = model.LastName;
        if (model.IsActive.HasValue) avatar.IsActive = model.IsActive.Value;

        return await _avatarStore.UpsertAsync(avatar, default);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid avatarId, AZOARequest? request = null)
    {
        // IDOR guard: an avatar may only delete its own record.
        if (id != avatarId)
            return new AZOAResult<bool> { IsError = true, Message = "You may only delete your own avatar." };

        return await _avatarStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<bool>> LogoutEverywhereAsync(Guid avatarId, CancellationToken ct = default)
    {
        // Subject comes from the authenticated token (controller), never a request body.
        var existing = await _avatarStore.GetByIdAsync(avatarId, ct);
        if (existing.IsError || existing.Result is null)
            return new AZOAResult<bool> { IsError = true, Message = "Avatar not found." };

        // The only stateless-JWT revocation lever: bump the watermark to now so every
        // token minted before this instant fails the OnTokenValidated check (Program.cs).
        existing.Result.AuthNotBefore = DateTime.UtcNow;

        var saved = await _avatarStore.UpsertAsync(existing.Result, ct);
        if (saved.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saved.Message };

        return new AZOAResult<bool> { Result = true, Message = "Logged out of all sessions." };
    }

    public async Task<AZOAResult<IAvatar>> AssignDappRoleAsync(
        Guid targetAvatarId, string role, bool actingIsOperator, bool actingCanManage, CancellationToken ct = default)
    {
        // Reject any value outside the canonical dapp:user/developer/manager set BEFORE
        // touching authority — an operator:admin-yielding value can never persist.
        if (!AzoaDappRoles.IsAssignableRole(role))
            return new AZOAResult<IAvatar>
            {
                IsError = true,
                Message = $"'{role}' is not an assignable DApp role. Allowed: {string.Join(", ", AzoaDappRoles.AssignableRoles)}."
            };

        var normalized = AzoaDappRoles.Normalize(role);

        // Authority ladder (fail-closed): operator may set anything (incl. manager, the
        // bootstrap path); a manager may set only developer/user; everyone else denied.
        if (actingIsOperator)
        {
            // full authority
        }
        else if (actingCanManage)
        {
            if (string.Equals(normalized, AzoaDappRoles.Manager, StringComparison.OrdinalIgnoreCase))
                return new AZOAResult<IAvatar>
                {
                    IsError = true,
                    Message = "A DApp manager may grant only developer or user roles; assigning manager requires an operator."
                };
        }
        else
        {
            return new AZOAResult<IAvatar>
            {
                IsError = true,
                Message = "Only an operator or a DApp manager may assign DApp roles."
            };
        }

        var existing = await _avatarStore.GetByIdAsync(targetAvatarId, ct);
        if (existing.IsError || existing.Result is null)
            return new AZOAResult<IAvatar> { IsError = true, Message = "Avatar not found." };

        existing.Result.DappRole = normalized;
        return await _avatarStore.UpsertAsync(existing.Result, ct);
    }

    private string GenerateJwt(IAvatar avatar, bool isBootstrapGovernor)
    {
        var key = _config.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, avatar.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, avatar.Email),
            new Claim(ClaimTypes.Name, avatar.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        StampDappRoleClaims(avatar, claims);

        if (isBootstrapGovernor)
            StampOperatorAdmin(claims);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// H2 admin-token mint bootstrap: stamps <see cref="AzoaScopes.Operator"/> +
    /// the interim <c>role=Admin</c> claim onto the JWT for exactly the
    /// configured seed avatar. See Services/Admin/AGENTS.md for the fail-closed
    /// rationale and NODE-HOST.md §8.9 for the operator procedure.
    /// </summary>
    private async Task<AZOAResult<bool>> ResolveBootstrapAuthorityAsync(
        IAvatar avatar,
        string? presentedSecret)
    {
        var options = _config.GetSection(AdminBootstrapOptions.SectionName).Get<AdminBootstrapOptions>()
                      ?? new AdminBootstrapOptions();

        var hasEmail = !string.IsNullOrWhiteSpace(options.SeedEmail);
        var hasSecret = !string.IsNullOrWhiteSpace(options.SeedSecret);

        if (!hasEmail && !hasSecret)
            return new AZOAResult<bool> { Result = false, Message = "Bootstrap is off." };

        if (hasEmail != hasSecret)
        {
            // Fail-closed: a PARTIAL config (one of the two set) never stamps.
            // In Production this is also treated as a hard misconfiguration —
            // SeedAdminHostedService already throws at boot for this case, so
            // reaching a live request with a partial config in Production means
            // config was hot-reloaded after boot; refuse here too rather than
            // silently ignore it.
            if (_environment.IsProduction())
                throw new InvalidOperationException(
                    "AdminBootstrap is misconfigured (SeedEmail/SeedSecret must both be set or both unset).");
            return new AZOAResult<bool> { Result = false, Message = "Bootstrap is misconfigured." };
        }

        if (_adminBootstrapStateStore is null)
            return new AZOAResult<bool> { IsError = true, Message = "Bootstrap state storage is unavailable." };

        var existing = await _adminBootstrapStateStore.GetAsync();
        if (existing.IsError)
            return new AZOAResult<bool> { IsError = true, Message = "Bootstrap state is unavailable." };

        var avatarLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatar.Id));
        if (existing.Result is not null)
            return new AZOAResult<bool>
            {
                Result = string.Equals(existing.Result.AvatarId, avatarLink, StringComparison.Ordinal),
                Message = "Bootstrap binding resolved.",
            };

        if (!string.Equals(avatar.Email, options.SeedEmail, StringComparison.OrdinalIgnoreCase)
            || !SecretEquals(options.SeedSecret!, presentedSecret))
        {
            return new AZOAResult<bool> { Result = false, Message = "Bootstrap proof was not accepted." };
        }

        var bound = await _adminBootstrapStateStore.BindOnceAsync(new AZOA.WebAPI.Persistence.SurrealDb.Models.AdminBootstrapState
        {
            Id = AZOA.WebAPI.Persistence.SurrealDb.Models.AdminBootstrapState.LocalId,
            AvatarId = avatarLink ?? string.Empty,
            ActivatedAt = DateTimeOffset.UtcNow,
        });
        if (bound.IsError || bound.Result is null)
            return new AZOAResult<bool> { IsError = true, Message = "Bootstrap state could not be recorded." };

        return new AZOAResult<bool>
        {
            Result = string.Equals(bound.Result.AvatarId, avatarLink, StringComparison.Ordinal),
            Message = "Bootstrap binding resolved.",
        };
    }

    private async Task<AZOAResult<bool>> ValidateEmailUpdateAsync(IAvatar avatar, string requestedEmail)
    {
        if (string.IsNullOrWhiteSpace(requestedEmail))
            return new AZOAResult<bool> { IsError = true, Message = "Email cannot be empty." };

        var all = await _avatarStore.GetAllAsync();
        if (all.IsError)
            return new AZOAResult<bool> { IsError = true, Message = "Email uniqueness could not be verified." };
        if (all.Result?.Any(candidate => candidate.Id != avatar.Id
                && string.Equals(candidate.Email, requestedEmail, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return new AZOAResult<bool> { IsError = true, Message = "An account with this email already exists." };
        }

        var options = _config.GetSection(AdminBootstrapOptions.SectionName).Get<AdminBootstrapOptions>()
                      ?? new AdminBootstrapOptions();
        if (!string.Equals(requestedEmail, options.SeedEmail, StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<bool> { Result = true, Message = "Email is valid." };

        if (_adminBootstrapStateStore is null)
            return new AZOAResult<bool> { IsError = true, Message = "Bootstrap state storage is unavailable." };

        var binding = await _adminBootstrapStateStore.GetAsync();
        if (binding.IsError)
            return new AZOAResult<bool> { IsError = true, Message = "Bootstrap state is unavailable." };

        var avatarLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatar.Id));
        if (binding.Result is null
            || !string.Equals(binding.Result.AvatarId, avatarLink, StringComparison.Ordinal))
        {
            return new AZOAResult<bool>
            {
                IsError = true,
                Message = "The configured bootstrap email cannot be claimed through profile update.",
            };
        }

        return new AZOAResult<bool> { Result = true, Message = "Email is valid." };
    }

    private static bool SecretEquals(string configuredSecret, string? presentedSecret)
    {
        if (string.IsNullOrEmpty(presentedSecret))
            return false;

        var expected = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configuredSecret));
        var actual = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presentedSecret));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void StampOperatorAdmin(List<Claim> claims)
    {
        claims.Add(new Claim("scope", AzoaScopes.Operator));
        claims.Add(new Claim("scope", AzoaScopes.NodeGovern));
        claims.Add(new Claim("role", "Admin"));
        claims.Add(new Claim(ClaimTypes.Role, "Admin"));
    }

    private static void StampDappRoleClaims(IAvatar avatar, List<Claim> claims)
    {
        var role = AzoaDappRoles.Normalize(avatar.DappRole);
        claims.Add(new Claim("dapp_role", role));

        if (AzoaDappRoles.CanDevelop(role))
        {
            claims.Add(new Claim("scope", AzoaScopes.DappDevelop));
        }

        if (AzoaDappRoles.CanManage(role))
        {
            claims.Add(new Claim("scope", AzoaScopes.DappManage));
        }
    }
}
