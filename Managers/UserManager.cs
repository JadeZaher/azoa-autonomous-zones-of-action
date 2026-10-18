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
    private readonly IHostEnvironment? _environment;

    public UserManager(
        IUserStore userStore,
        IConfiguration config,
        IAdminBootstrapStateStore? adminBootstrapStateStore = null,
        IHostEnvironment? environment = null)
    {
        _userStore = userStore;
        _config = config;
        _adminBootstrapStateStore = adminBootstrapStateStore;
        _environment = environment;
    }

    public async Task<AZOAResult<IUser>> RegisterAsync(UserRegisterModel model, AZOARequest? request = null)
    {
        // Second of the two seams. The controller refuses first; this one is what a
        // future non-HTTP caller inherits, so the gate cannot be routed around.
        if (!SelfRegistrationPolicy.IsAllowed(_config, _environment))
            return AZOAResult<IUser>.FailureWithCode(SelfRegistrationPolicy.DisabledMessage, SelfRegistrationPolicy.DisabledCode);

        var email = NormalizeIdentityValue(model.Email);
        var username = NormalizeIdentityValue(model.Username);
        if (string.Equals(email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase))
            return new AZOAResult<IUser> { IsError = true, Message = "This account identity is reserved." };
        if (email.Length == 0 || username.Length == 0)
            return new AZOAResult<IUser> { IsError = true, Message = "A username and email are required." };
        if (UsernameRules.IsReservedGeneratedHandle(username))
            return AZOAResult<IUser>.FailureWithCode(UsernameRules.ReservedPrefixMessage, AzoaErrorCodes.InvalidRequest);

        var user = new User
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password),
            Title = model.Title,
            FirstName = model.FirstName,
            LastName = model.LastName
        };

        // Uniqueness is the database's job, not a read-then-write scan: `user_email`
        // and `user_username` are UNIQUE indexes (Persistence/SurrealDb/Generated/
        // Schemas/user.surql). The old O(n) GetAllAsync scan loaded every user on
        // every registration AND still let two concurrent registrations both pass it.
        // The write below carries a fresh id, so it is a create the index adjudicates.
        var saved = await _userStore.UpsertAsync(user, default);
        if (!saved.IsError)
            return saved;

        // Off the happy path only, and deliberately WITHOUT a read. The store maps
        // every write failure onto one opaque dependency error, so naming which
        // field collided would need a lookup IUserStore does not expose - and the
        // lookup that used to be here was GetAllAsync, an unbounded table scan an
        // anonymous caller could force on every failed registration. That put the
        // O(n) scan back on the very endpoint it had just been removed from, and it
        // told a stranger whether an address is registered. One message is true
        // whether the index refused the write or the database was unreachable, and
        // it names nobody. A targeted store lookup would restore the field-level
        // message - see docs/NODE-HOST.md section 8.10.
        return new AZOAResult<IUser>
        {
            IsError = true,
            Message = RegistrationRefusedMessage,
            Code = saved.Code,
        };
    }

    /// <summary>Refusal for a rejected registration write; never names who holds the identity.</summary>
    public const string RegistrationRefusedMessage =
        "This account could not be created. The email or username may already be in use.";

    /// <summary>Trimmed, lower-invariant identity value; the UNIQUE indexes match exactly.</summary>
    private static string NormalizeIdentityValue(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    public async Task<AZOAResult<string>> LoginAsync(UserLoginModel model, AZOARequest? request = null)
    {
        if (model.UserId.HasValue && !string.IsNullOrWhiteSpace(model.Email))
            return AZOAResult<string>.Failure("Provide one login identity.");
        IUser? user;
        if (model.UserId is { } userId)
        {
            var loaded = await _userStore.GetByIdAsync(userId, default);
            if (loaded.IsError) return AZOAResult<string>.Failure(loaded.Message);
            user = loaded.Result;
        }
        else
        {
            var all = await _userStore.GetAllAsync(default);
            if (all.IsError) return AZOAResult<string>.Failure(all.Message);
            user = ResolveLoginSubject(all.Result, model.Email);
        }

        if (user == null || !user.IsActive || user.OwnerTenantId is not null || !BCrypt.Net.BCrypt.Verify(model.Password, user.PasswordHash))
            return new AZOAResult<string> { IsError = true, Message = "Invalid credentials." };
        if (user.Id == NodeOperatorIdentity.UserId)
            return new AZOAResult<string> { IsError = true, Message = "Invalid credentials." };

        var token = GenerateJwt(user);
        return new AZOAResult<string> { Result = token, Message = "Login successful." };
    }

    /// <summary>The single row this address may sign in as, or null when it is ambiguous.</summary>
    /// <remarks>
    /// <c>user_email</c> is a case-SENSITIVE UNIQUE index, so two rows can differ only in
    /// case. Every writer folds case TODAY (<c>TenantManager</c> included, since the
    /// tenant-provisioned path was fixed), but rows written before that still exist and this
    /// branch is what keeps them unambiguous. Taking the first case-insensitive match would
    /// then let row order decide which of two password hashes an address authenticates
    /// against. Exact match wins; a lone case-insensitive match still resolves, for
    /// rows written before folding; two or more resolve to nothing.
    /// </remarks>
    private static IUser? ResolveLoginSubject(IEnumerable<IUser>? users, string? email)
    {
        var candidate = email?.Trim();
        if (users is null || string.IsNullOrEmpty(candidate))
            return null;

        var folded = users
            .Where(row => string.Equals(row.Email, candidate, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var exact = folded.FirstOrDefault(row =>
            string.Equals(row.Email, candidate, StringComparison.Ordinal));

        return exact ?? (folded.Count == 1 ? folded[0] : null);
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
        // Normalise BEFORE validating and before persisting. A profile update writes
        // the same case-SENSITIVE user_email / user_username UNIQUE indexes that
        // registration writes, so persisting the raw value let a user re-case their
        // own address straight past the index and leave two rows differing only in
        // case - exactly the ambiguity ResolveLoginSubject then has to refuse.
        var requestedEmail = model.Email is null ? null : NormalizeIdentityValue(model.Email);
        var requestedUsername = model.Username is null ? null : NormalizeIdentityValue(model.Username);
        if (requestedEmail is not null)
        {
            var emailValidation = await ValidateEmailUpdateAsync(user, requestedEmail);
            if (emailValidation.IsError)
                return new AZOAResult<IUser> { IsError = true, Message = emailValidation.Message };
        }
        if (requestedUsername is not null)
        {
            var usernameValidation = await ValidateUsernameUpdateAsync(user, requestedUsername);
            if (usernameValidation.IsError)
                return new AZOAResult<IUser> { IsError = true, Message = usernameValidation.Message };
        }
        if (requestedUsername != null) user.Username = requestedUsername;
        if (requestedEmail != null) user.Email = requestedEmail;
        if (model.Title != null) user.Title = model.Title;
        if (model.FirstName != null) user.FirstName = model.FirstName;
        if (model.LastName != null) user.LastName = model.LastName;
        if (model.Discoverable.HasValue) user.Discoverable = model.Discoverable.Value;
        var profile = await _userStore.UpsertAsync(user, default);
        if (profile.IsError || !model.IsActive.HasValue || model.IsActive.Value == user.IsActive) return profile;
        return await _userStore.TrySetActiveAsync(user.Id, user.IsActive, model.IsActive.Value, default);
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
        var saved = await _userStore.AdvanceAuthenticationWatermarkAsync(userId, DateTime.UtcNow, ct);
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
        if (!actingIsOperator && AzoaDappRoles.CanManage(existing.Result.DappRole))
            return new AZOAResult<IUser>
            {
                IsError = true,
                Message = "Changing a current DApp manager's role requires an operator."
            };

        return await _userStore.TryAssignDappRoleAsync(targetUserId, existing.Result.DappRole, normalized, ct);
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
        var normalized = NormalizeIdentityValue(requestedUsername);
        if (string.IsNullOrWhiteSpace(normalized))
            return new AZOAResult<bool> { IsError = true, Message = "Username cannot be empty." };
        // Keeping a generated handle is fine; adopting someone else's would squat the
        // deterministic name their first wallet/OAuth sign-in is about to mint.
        if (!string.Equals(normalized, user.Username, StringComparison.OrdinalIgnoreCase)
            && UsernameRules.IsReservedGeneratedHandle(normalized))
            return new AZOAResult<bool> { IsError = true, Message = UsernameRules.ReservedPrefixMessage };

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

/// <summary>
/// Whether an anonymous caller may create an account on this node: password registration,
/// first wallet sign-in and first federated sign-in all ask this one switch.
/// </summary>
/// <remarks>
/// The endpoint is <c>[AllowAnonymous]</c> and the same node custodies another
/// product's real users, so "nobody self-registers here" has to be a control, not
/// a habit. Two keys, because a node inherits the base appsettings verbatim: the
/// node-wide switch turns it off anywhere, and Production additionally demands an
/// explicit acknowledgement before an anonymous stranger may create an identity.
/// See <c>docs/NODE-HOST.md</c> section 3.
/// </remarks>
public static class SelfRegistrationPolicy
{
    /// <summary>Node-wide switch. Absent means enabled outside Production.</summary>
    public const string EnabledKey = "Registration:Enabled";

    /// <summary>Production-only acknowledgement. Absent or false refuses in Production.</summary>
    public const string ProductionAcknowledgementKey =
        "Registration:AllowPublicSelfServiceInProduction";

    /// <summary>Refusal surfaced to an anonymous caller; states a node fact, never a user fact.</summary>
    public const string DisabledMessage =
        "Public self-registration is disabled on this node. Accounts are provisioned by the operator.";

    /// <summary>Wire code for the refusal, shared by password, wallet and federated sign-up.</summary>
    public const string DisabledCode = "SELF_REGISTRATION_DISABLED";

    /// <summary>True only when the node allows anonymous account creation right now.</summary>
    public static bool IsAllowed(IConfiguration? configuration, IHostEnvironment? environment)
    {
        if (configuration is null)
            return environment?.IsProduction() != true;
        // Parsed rather than bound. An absent switch means "not configured", which is
        // enabled outside Production; a PRESENT but unparseable one reads as OFF,
        // because a typo on this key must never open anonymous account creation.
        var switchValue = configuration[EnabledKey];
        if (switchValue is not null && (!bool.TryParse(switchValue, out var enabled) || !enabled))
            return false;
        if (environment?.IsProduction() != true)
            return true;

        return bool.TryParse(configuration[ProductionAcknowledgementKey], out var acknowledged)
            && acknowledged;
    }
}
