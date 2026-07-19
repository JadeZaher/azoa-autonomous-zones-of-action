using System.Security.Claims;
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
    private readonly IAdminBootstrapStateStore? _adminBootstrapStateStore;

    public AvatarManager(
        IAvatarStore avatarStore,
        IConfiguration config,
        IAdminBootstrapStateStore? adminBootstrapStateStore = null)
    {
        _avatarStore = avatarStore;
        _config = config;
        _adminBootstrapStateStore = adminBootstrapStateStore;
    }

    public async Task<AZOAResult<IAvatar>> RegisterAsync(AvatarRegisterModel model, AZOARequest? request = null)
    {
        if (string.Equals(model.Email?.Trim(), NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<IAvatar> { IsError = true, Message = "This account identity is reserved." };

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
        if (avatar.Id == NodeOperatorIdentity.AvatarId)
            return new AZOAResult<string> { IsError = true, Message = "Invalid credentials." };

        var token = GenerateJwt(avatar);
        return new AZOAResult<string> { Result = token, Message = "Login successful." };
    }

    public async Task<AZOAResult<IAvatar>> GetAsync(Guid id, AZOARequest? request = null)
    {
        if (id == NodeOperatorIdentity.AvatarId)
            return new AZOAResult<IAvatar> { IsError = true, Message = "Avatar not found." };
        return await _avatarStore.GetByIdAsync(id, default);
    }

    public async Task<AZOAResult<IEnumerable<IAvatar>>> GetAllAsync(AZOARequest? request = null)
    {
        var result = await _avatarStore.GetAllAsync(default);
        if (!result.IsError && result.Result is not null)
            result.Result = result.Result.Where(avatar => avatar.Id != NodeOperatorIdentity.AvatarId).ToList();
        return result;
    }

    public async Task<AZOAResult<IAvatar>> UpdateAsync(Guid id, AvatarUpdateModel model, Guid avatarId, AZOARequest? request = null)
    {
        // IDOR guard: an avatar may only edit its own record — the route id
        // must match the authenticated avatar identity.
        if (id != avatarId)
            return new AZOAResult<IAvatar> { IsError = true, Message = "You may only update your own avatar." };
        if (id == NodeOperatorIdentity.AvatarId)
            return new AZOAResult<IAvatar> { IsError = true, Message = "The node operator identity is managed only through operator credential rotation." };

        var existing = await _avatarStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;

        var avatar = existing.Result;
        if (model.Email is not null)
        {
            var emailValidation = await ValidateEmailUpdateAsync(avatar, model.Email);
            if (emailValidation.IsError)
                return new AZOAResult<IAvatar> { IsError = true, Message = emailValidation.Message };
        }
        if (model.Username is not null)
        {
            var usernameValidation = await ValidateUsernameUpdateAsync(avatar, model.Username);
            if (usernameValidation.IsError)
                return new AZOAResult<IAvatar> { IsError = true, Message = usernameValidation.Message };
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
        if (id == NodeOperatorIdentity.AvatarId)
            return new AZOAResult<bool> { IsError = true, Message = "The node operator identity cannot be deleted through the avatar API." };

        return await _avatarStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<bool>> LogoutEverywhereAsync(Guid avatarId, CancellationToken ct = default)
    {
        if (avatarId == NodeOperatorIdentity.AvatarId)
            return new AZOAResult<bool> { IsError = true, Message = "Use the node operator session revocation endpoint." };
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
        if (targetAvatarId == NodeOperatorIdentity.AvatarId)
            return new AZOAResult<IAvatar>
            {
                IsError = true,
                Message = "The node operator identity cannot receive DApp roles."
            };

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

    private string GenerateJwt(IAvatar avatar)
    {
        var key = _config.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, avatar.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, avatar.Email),
            new Claim(ClaimTypes.Name, avatar.Username),
            new Claim(AzoaClaims.TokenUse, AzoaClaims.TokenUseLogin),
            new Claim(
                AzoaClaims.AuthTime,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        StampDappRoleClaims(avatar, claims);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<AZOAResult<bool>> ValidateEmailUpdateAsync(IAvatar avatar, string requestedEmail)
    {
        if (string.IsNullOrWhiteSpace(requestedEmail))
            return new AZOAResult<bool> { IsError = true, Message = "Email cannot be empty." };
        if (avatar.Id != NodeOperatorIdentity.AvatarId
            && string.Equals(requestedEmail.Trim(), NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
        {
            return new AZOAResult<bool> { IsError = true, Message = "This account identity is reserved." };
        }

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

    private async Task<AZOAResult<bool>> ValidateUsernameUpdateAsync(IAvatar avatar, string requestedUsername)
    {
        var normalized = requestedUsername.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return new AZOAResult<bool> { IsError = true, Message = "Username cannot be empty." };

        var all = await _avatarStore.GetAllAsync();
        if (all.IsError)
            return new AZOAResult<bool> { IsError = true, Message = "Username uniqueness could not be verified." };
        if (all.Result?.Any(candidate => candidate.Id != avatar.Id
                && string.Equals(candidate.Username, normalized, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return new AZOAResult<bool> { IsError = true, Message = "This username is already taken." };
        }

        var binding = _adminBootstrapStateStore is null
            ? null
            : await _adminBootstrapStateStore.GetAsync();
        if (binding?.Result?.CredentialRevision > 0)
        {
            var reserved = await _avatarStore.GetByIdAsync(NodeOperatorIdentity.AvatarId);
            if (!reserved.IsError
                && reserved.Result is not null
                && string.Equals(reserved.Result.Username, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return new AZOAResult<bool> { IsError = true, Message = "This username is reserved." };
            }
        }

        return new AZOAResult<bool> { Result = true, Message = "Username is valid." };
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
