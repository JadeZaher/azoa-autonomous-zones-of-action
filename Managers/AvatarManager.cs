using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Managers;

public class AvatarManager : IAvatarManager
{
    private readonly ProviderContext _providerContext;
    private readonly IConfiguration _config;

    public AvatarManager(ProviderContext providerContext, IConfiguration config)
    {
        _providerContext = providerContext;
        _config = config;
    }

    public async Task<OASISResult<IAvatar>> RegisterAsync(AvatarRegisterModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatar> { IsError = true, Message = activation.Message };

        var avatar = new Avatar
        {
            Username = model.Username,
            Email = model.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(model.Password),
            Title = model.Title,
            FirstName = model.FirstName,
            LastName = model.LastName
        };

        return await _providerContext.CurrentProvider.SaveAvatarAsync(avatar);
    }

    public async Task<OASISResult<string>> LoginAsync(AvatarLoginModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<string> { IsError = true, Message = activation.Message };

        var all = await _providerContext.CurrentProvider.LoadAllAvatarsAsync();
        var avatar = all.Result?.FirstOrDefault(a => a.Email.Equals(model.Email, StringComparison.OrdinalIgnoreCase));

        if (avatar == null || !BCrypt.Net.BCrypt.Verify(model.Password, avatar.PasswordHash))
            return new OASISResult<string> { IsError = true, Message = "Invalid credentials." };

        var token = GenerateJwt(avatar);
        return new OASISResult<string> { Result = token, Message = "Login successful." };
    }

    public async Task<OASISResult<IAvatar>> GetAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatar> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAvatarAsync(id);
    }

    public async Task<OASISResult<IEnumerable<IAvatar>>> GetAllAsync(OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IAvatar>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadAllAvatarsAsync();
    }

    public async Task<OASISResult<IAvatar>> UpdateAsync(Guid id, AvatarUpdateModel model, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IAvatar> { IsError = true, Message = activation.Message };

        var existing = await _providerContext.CurrentProvider.LoadAvatarAsync(id);
        if (existing.IsError || existing.Result == null) return existing;

        var avatar = existing.Result;
        if (model.Username != null) avatar.Username = model.Username;
        if (model.Email != null) avatar.Email = model.Email;
        if (model.Title != null) avatar.Title = model.Title;
        if (model.FirstName != null) avatar.FirstName = model.FirstName;
        if (model.LastName != null) avatar.LastName = model.LastName;
        if (model.IsActive.HasValue) avatar.IsActive = model.IsActive.Value;

        return await _providerContext.CurrentProvider.SaveAvatarAsync(avatar);
    }

    public async Task<OASISResult<bool>> DeleteAsync(Guid id, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.DeleteAvatarAsync(id);
    }

    public async Task<OASISResult<IWallet>> AddWalletAsync(Guid avatarId, Wallet wallet, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IWallet> { IsError = true, Message = activation.Message };

        wallet.AvatarId = avatarId;
        return await _providerContext.CurrentProvider.SaveWalletAsync(wallet);
    }

    public async Task<OASISResult<bool>> RemoveWalletAsync(Guid avatarId, Guid walletId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<bool> { IsError = true, Message = activation.Message };

        var wallet = await _providerContext.CurrentProvider.LoadWalletAsync(walletId);
        if (wallet.IsError || wallet.Result == null || wallet.Result.AvatarId != avatarId)
            return new OASISResult<bool> { IsError = true, Message = "Wallet not found or not owned by avatar.", Result = false };

        return await _providerContext.CurrentProvider.DeleteWalletAsync(walletId);
    }

    public async Task<OASISResult<IEnumerable<IWallet>>> GetWalletsAsync(Guid avatarId, OASISRequest? request = null)
    {
        var activation = _providerContext.Activate(request);
        if (activation.IsError) return new OASISResult<IEnumerable<IWallet>> { IsError = true, Message = activation.Message };

        return await _providerContext.CurrentProvider.LoadWalletsByAvatarAsync(avatarId);
    }

    private string GenerateJwt(IAvatar avatar)
    {
        var key = _config.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key missing.");
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
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
