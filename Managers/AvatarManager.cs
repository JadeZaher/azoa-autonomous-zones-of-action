using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Managers;

public class AvatarManager : IAvatarManager
{
    private readonly IAvatarStore _avatarStore;
    private readonly IConfiguration _config;

    public AvatarManager(IAvatarStore avatarStore, IConfiguration config)
    {
        _avatarStore = avatarStore;
        _config = config;
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

        var token = GenerateJwt(avatar);
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

    private string GenerateJwt(IAvatar avatar)
    {
        var key = _config.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, avatar.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, avatar.Email),
            new Claim(ClaimTypes.Name, avatar.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
