using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using TextEncoding = System.Text.Encoding;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Admin;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SurrealForge.Client;

namespace AZOA.WebAPI.Managers;

/// <summary>Authenticates only the durable node-operator binding and emits short sessions.</summary>
public sealed class NodeOperatorManager : INodeOperatorManager
{
    private const string InvalidCredentials = "Invalid node operator credentials.";
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(
        "azoa-dummy-operator-credential-never-valid", workFactor: 12);
    private readonly IAdminBootstrapStateStore _state;
    private readonly IAvatarStore _avatars;
    private readonly NodeOperatorOptions _options;
    private readonly IConfiguration _configuration;
    private readonly NodeOperatorLoginThrottle _throttle;

    public NodeOperatorManager(
        IAdminBootstrapStateStore state,
        IAvatarStore avatars,
        IOptions<NodeOperatorOptions> options,
        IConfiguration configuration,
        NodeOperatorLoginThrottle throttle)
    {
        _state = state;
        _avatars = avatars;
        _options = options.Value;
        _configuration = configuration;
        _throttle = throttle;
    }

    public async Task<AZOAResult<NodeOperatorSessionResponse>> LoginAsync(
        NodeOperatorLoginRequest request,
        string clientAddress,
        CancellationToken ct = default)
    {
        var rawUsername = request?.Username ?? string.Empty;
        var suppliedPassword = request?.Password ?? string.Empty;
        var username = NodeOperatorIdentity.NormalizeUsername(rawUsername);
        if (rawUsername.Length > 128
            || suppliedPassword.Length > 256
            || TextEncoding.UTF8.GetByteCount(suppliedPassword) > 72
            || rawUsername.Any(char.IsControl)
            || suppliedPassword.Any(char.IsControl))
        {
            _ = BCrypt.Net.BCrypt.Verify("invalid", DummyHash);
            return AZOAResult<NodeOperatorSessionResponse>.Failure(InvalidCredentials);
        }
        var throttle = _throttle.TryAcquire(clientAddress, username, DateTimeOffset.UtcNow);
        if (!throttle.Allowed)
        {
            _ = BCrypt.Net.BCrypt.Verify(suppliedPassword, DummyHash);
            return new AZOAResult<NodeOperatorSessionResponse>
            {
                IsError = true,
                Message = "Node operator sign-in is temporarily unavailable. Try again after the cooldown.",
                Code = NodeOperatorErrorCodes.LoginThrottled,
                RetryAfterSeconds = throttle.RetryAfterSeconds,
            };
        }

        AZOAResult<AdminBootstrapState?> state;
        try
        {
            state = await _state.GetAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _ = BCrypt.Net.BCrypt.Verify(suppliedPassword, DummyHash);
            return ServiceUnavailable();
        }
        if (state.IsError || state.Result is null || state.Result.CredentialRevision < 1)
        {
            _ = BCrypt.Net.BCrypt.Verify(suppliedPassword, DummyHash);
            return ServiceUnavailable();
        }

        AZOAResult<AZOA.WebAPI.Interfaces.IAvatar> avatar;
        try
        {
            avatar = await _avatars.GetByIdAsync(NodeOperatorIdentity.AvatarId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _ = BCrypt.Net.BCrypt.Verify(suppliedPassword, DummyHash);
            return ServiceUnavailable();
        }
        var passwordHash = avatar.Result?.PasswordHash ?? DummyHash;
        var passwordMatches = NodeOperatorIdentity.VerifyPassword(suppliedPassword, passwordHash);
        var expectedLink = SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(NodeOperatorIdentity.AvatarId));
        if (avatar.IsError
            || avatar.Result is null
            || !avatar.Result.IsActive
            || !string.Equals(state.Result.AvatarId, expectedLink, StringComparison.Ordinal)
            || !string.Equals(avatar.Result.Email, NodeOperatorIdentity.ReservedEmail, StringComparison.OrdinalIgnoreCase)
            || !NodeOperatorIdentity.IsValidUsername(avatar.Result.Username)
            || !NodeOperatorIdentity.IsStructurallyValidPasswordHash(avatar.Result.PasswordHash))
        {
            return ServiceUnavailable();
        }
        if (!string.Equals(avatar.Result.Username, username, StringComparison.Ordinal)
            || !passwordMatches)
        {
            return AZOAResult<NodeOperatorSessionResponse>.Failure(InvalidCredentials);
        }

        _throttle.Reset(clientAddress, username);
        var authenticatedAt = DateTimeOffset.UtcNow;
        var expiresAt = authenticatedAt.AddMinutes(Math.Clamp(_options.SessionMinutes, 5, 30));
        var token = CreateToken(
            avatar.Result.Username,
            state.Result.CredentialRevision,
            state.Result.SessionRevision,
            authenticatedAt,
            expiresAt);
        return AZOAResult<NodeOperatorSessionResponse>.Success(new NodeOperatorSessionResponse
        {
            AccessToken = token,
            ExpiresAt = expiresAt,
            Username = avatar.Result.Username,
        }, "Node operator authenticated.");
    }

    public async Task<AZOAResult<bool>> RevokeAllSessionsAsync(CancellationToken ct = default)
    {
        var avatar = await _avatars.GetByIdAsync(NodeOperatorIdentity.AvatarId, ct);
        if (avatar.IsError || avatar.Result is null)
            return AZOAResult<bool>.Failure("Node operator identity is unavailable.", false);
        var state = await _state.GetAsync(ct);
        if (state.IsError || state.Result is null)
            return AZOAResult<bool>.Failure("Node operator identity is unavailable.", false);
        var saved = await _state.AdvanceSessionWatermarkAsync(
            state.Result.CredentialRevision,
            state.Result.SessionRevision,
            DateTimeOffset.UtcNow,
            ct);
        return saved.IsError
            ? AZOAResult<bool>.Failure("Node operator sessions could not be revoked.", false)
            : AZOAResult<bool>.Success(true, "All node operator sessions were revoked.");
    }

    private static AZOAResult<NodeOperatorSessionResponse> ServiceUnavailable()
        => AZOAResult<NodeOperatorSessionResponse>.FailureWithCode(
            "Node operator sign-in is temporarily unavailable. Try again later.",
            NodeOperatorErrorCodes.ServiceUnavailable);

    private string CreateToken(
        string username,
        long revision,
        long sessionRevision,
        DateTimeOffset authenticatedAt,
        DateTimeOffset expiresAt)
    {
        var key = _configuration["Jwt:Key"]
            ?? throw new InvalidOperationException("JWT Key missing.");
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(TextEncoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, NodeOperatorIdentity.AvatarId.ToString("D")),
            new(ClaimTypes.Name, username),
            new(AzoaClaims.TokenUse, AzoaClaims.TokenUseNodeOperator),
            new(AzoaClaims.OperatorRevision, revision.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(AzoaClaims.OperatorSessionRevision, sessionRevision.ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(AzoaClaims.AuthTime, authenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new("scope", AzoaScopes.Operator),
            new("scope", AzoaScopes.NodeGovern),
        };
        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            notBefore: authenticatedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
