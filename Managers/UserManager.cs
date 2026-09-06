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

public class UserManager : IUserManager
{
    private readonly IUserStore _userStore;
    private readonly IConfiguration _config;
    private readonly IAdminBootstrapStateStore? _adminBootstrapStateStore;

    public UserManager(
        IUserStore userStore,
        IConfiguration config,
        IAdminBootstrapStateStore? adminBootstrapStateStore = null)
    {
        _userStore = userStore;
        _config = config;
        _adminBootstrapStateStore = adminBootstrapStateStore;
    }

    public async Task<AZOAResult<IUser>> RegisterAsync(UserRegisterModel model, AZOARequest? request = null)
    {
        if (string.Equals(model.Email?.Trim(), NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<IUser> { IsError = true, Message = "This account identity is reserved." };

        // Check for duplicate email
        var allUsers = await _userStore.GetAllAsync(default);
        if (allUsers.Result?.Any(a => a.Email.Equals(model.Email, StringComparison.OrdinalIgnoreCase)) == true)
            return new AZOAResult<IUser> { IsError = true, Message = "An account with this email already exists." };

        // Check for duplicate username
        if (allUsers.Result?.Any(a => a.Username.Equals(model.Username, StringComparison.OrdinalIgnoreCase)) == true)
            return new AZOAResult<IUser> { IsError = true, Message = "This username is already taken." };

        var user = new User
        {
            Username = model.Username,
            Email = model.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password),
            Title = model.Title,
            FirstName = model.FirstName,
            LastName = model.LastName
        };

        return await _userStore.UpsertAsync(user, default);
    }

    public async Task<AZOAResult<string>> LoginAsync(UserLoginModel model, AZOARequest? request = null)
    {
        var all = await _userStore.GetAllAsync(default);
        var user = all.Result?.FirstOrDefault(a => a.Email.Equals(model.Email, StringComparison.OrdinalIgnoreCase));

        if (user == null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
            return new AZOAResult<string> { IsError = true, Message = "Invalid credentials." };
        if (user.Id == NodeOperatorIdentity.UserId)
            return new AZOAResult<string> { IsError = true, Message = "Invalid credentials." };

        var token = GenerateJwt(user);
        return new AZOAResult<string> { Result = token, Message = "Login successful." };
    }

    public async Task<AZOAResult<IUser>> GetAsync(Guid id, AZOARequest? request = null)
    {
        if (id == NodeOperatorIdentity.UserId)
            return new AZOAResult<IUser> { IsError = true, Message = "User not found." };
        return await _userStore.GetByIdAsync(id, default);
    }

    public async Task<AZOAResult<IEnumerable<IUser>>> GetAllAsync(AZOARequest? request = null)
    {
        var result = await _userStore.GetAllAsync(default);
        if (!result.IsError && result.Result is not null)
            result.Result = result.Result.Where(user => user.Id != NodeOperatorIdentity.UserId).ToList();
        return result;
    }

    public async Task<AZOAResult<IUser>> UpdateAsync(Guid id, UserUpdateModel model, Guid userId, AZOARequest? request = null)
    {
        // IDOR guard: a user may only edit its own record — the route id
        // must match the authenticated user identity.
        if (id != userId)
            return new AZOAResult<IUser> { IsError = true, Message = "You may only update your own user." };
        if (id == NodeOperatorIdentity.UserId)
            return new AZOAResult<IUser> { IsError = true, Message = "The node operator identity is managed only through operator credential rotation." };

        var existing = await _userStore.GetByIdAsync(id, default);
        if (existing.IsError || existing.Result == null) return existing;

        var user = existing.Result;
        if (model.Email is not null)
        {
            var emailValidation = await ValidateEmailUpdateAsync(user, model.Email);
            if (emailValidation.IsError)
                return new AZOAResult<IUser> { IsError = true, Message = emailValidation.Message };
        }
        if (model.Username is not null)
        {
            var usernameValidation = await ValidateUsernameUpdateAsync(user, model.Username);
            if (usernameValidation.IsError)
                return new AZOAResult<IUser> { IsError = true, Message = usernameValidation.Message };
        }
        if (model.Username != null) user.Username = model.Username;
        if (model.Email != null) user.Email = model.Email;
        if (model.Title != null) user.Title = model.Title;
        if (model.FirstName != null) user.FirstName = model.FirstName;
        if (model.LastName != null) user.LastName = model.LastName;
        if (model.IsActive.HasValue) user.IsActive = model.IsActive.Value;

        return await _userStore.UpsertAsync(user, default);
    }

    public async Task<AZOAResult<bool>> DeleteAsync(Guid id, Guid userId, AZOARequest? request = null)
    {
        // IDOR guard: a user may only delete its own record.
        if (id != userId)
            return new AZOAResult<bool> { IsError = true, Message = "You may only delete your own user." };
        if (id == NodeOperatorIdentity.UserId)
            return new AZOAResult<bool> { IsError = true, Message = "The node operator identity cannot be deleted through the user API." };

        return await _userStore.DeleteAsync(id, default);
    }

    public async Task<AZOAResult<bool>> LogoutEverywhereAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == NodeOperatorIdentity.UserId)
            return new AZOAResult<bool> { IsError = true, Message = "Use the node operator session revocation endpoint." };
        // Subject comes from the authenticated token (controller), never a request body.
        var existing = await _userStore.GetByIdAsync(userId, ct);
        if (existing.IsError || existing.Result is null)
            return new AZOAResult<bool> { IsError = true, Message = "User not found." };

        // The only stateless-JWT revocation lever: bump the watermark to now so every
        // token minted before this instant fails the OnTokenValidated check (Program.cs).
        existing.Result.AuthNotBefore = DateTime.UtcNow;

        var saved = await _userStore.UpsertAsync(existing.Result, ct);
        if (saved.IsError)
            return new AZOAResult<bool> { IsError = true, Message = saved.Message };

        return new AZOAResult<bool> { Result = true, Message = "Logged out of all sessions." };
    }

    public async Task<AZOAResult<IUser>> AssignDappRoleAsync(
        Guid targetUserId, string role, bool actingIsOperator, bool actingCanManage, CancellationToken ct = default)
    {
        // Reject any value outside the canonical dapp:user/developer/manager set BEFORE
        // touching authority — an operator:admin-yielding value can never persist.
        if (!AzoaDappRoles.IsAssignableRole(role))
            return new AZOAResult<IUser>
            {
                IsError = true,
                Message = $"'{role}' is not an assignable DApp role. Allowed: {string.Join(", ", AzoaDappRoles.AssignableRoles)}."
            };

        var normalized = AzoaDappRoles.Normalize(role);
        if (targetUserId == NodeOperatorIdentity.UserId)
            return new AZOAResult<IUser>
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
                return new AZOAResult<IUser>
                {
                    IsError = true,
                    Message = "A DApp manager may grant only developer or user roles; assigning manager requires an operator."
                };
        }
        else
        {
            return new AZOAResult<IUser>
            {
                IsError = true,
                Message = "Only an operator or a DApp manager may assign DApp roles."
            };
        }

        var existing = await _userStore.GetByIdAsync(targetUserId, ct);
        if (existing.IsError || existing.Result is null)
            return new AZOAResult<IUser> { IsError = true, Message = "User not found." };

        existing.Result.DappRole = normalized;
        return await _userStore.UpsertAsync(existing.Result, ct);
    }

    /// <summary>Mints the password path's login token via the one shared issuer.</summary>
    /// <remarks>
    /// The body moved to <see cref="AZOA.WebAPI.Services.Auth.UserLoginTokenIssuer"/>
    /// so federated sign-in issues a byte-for-byte equivalent credential instead of a
    /// hand-copied near-miss. See that type's remarks for the drift this prevents.
    /// </remarks>
    private string GenerateJwt(IUser user)
        => AZOA.WebAPI.Services.Auth.UserLoginTokenIssuer.BuildLoginToken(_config, user);

    private async Task<AZOAResult<bool>> ValidateEmailUpdateAsync(IUser user, string requestedEmail)
    {
        if (string.IsNullOrWhiteSpace(requestedEmail))
            return new AZOAResult<bool> { IsError = true, Message = "Email cannot be empty." };
        if (user.Id != NodeOperatorIdentity.UserId
            && string.Equals(requestedEmail.Trim(), NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
        {
            return new AZOAResult<bool> { IsError = true, Message = "This account identity is reserved." };
        }

        var all = await _userStore.GetAllAsync();
        if (all.IsError)
            return new AZOAResult<bool> { IsError = true, Message = "Email uniqueness could not be verified." };
        if (all.Result?.Any(candidate => candidate.Id != user.Id
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

        var userLink = SurrealLink.ToLink("user", SurrealId.ToSurrealId(user.Id));
        if (binding.Result is null
            || !string.Equals(binding.Result.UserId, userLink, StringComparison.Ordinal))
        {
            return new AZOAResult<bool>
            {
                IsError = true,
                Message = "The configured bootstrap email cannot be claimed through profile update.",
            };
        }

        return new AZOAResult<bool> { Result = true, Message = "Email is valid." };
    }

    private async Task<AZOAResult<bool>> ValidateUsernameUpdateAsync(IUser user, string requestedUsername)
    {
        var normalized = requestedUsername.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return new AZOAResult<bool> { IsError = true, Message = "Username cannot be empty." };

        var all = await _userStore.GetAllAsync();
        if (all.IsError)
            return new AZOAResult<bool> { IsError = true, Message = "Username uniqueness could not be verified." };
        if (all.Result?.Any(candidate => candidate.Id != user.Id
                && string.Equals(candidate.Username, normalized, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return new AZOAResult<bool> { IsError = true, Message = "This username is already taken." };
        }

        var binding = _adminBootstrapStateStore is null
            ? null
            : await _adminBootstrapStateStore.GetAsync();
        if (binding?.Result?.CredentialRevision > 0)
        {
            var reserved = await _userStore.GetByIdAsync(NodeOperatorIdentity.UserId);
            if (!reserved.IsError
                && reserved.Result is not null
                && string.Equals(reserved.Result.Username, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return new AZOAResult<bool> { IsError = true, Message = "This username is reserved." };
            }
        }

        return new AZOAResult<bool> { Result = true, Message = "Username is valid." };
    }

}
