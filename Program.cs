using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Core.Decorators;
using OASIS.WebAPI.Core.ProviderSelection;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Managers;
using FluentValidation.AspNetCore;
using OASIS.WebAPI.Providers;
using OASIS.WebAPI.Providers.Blockchain.Algorand;
using OASIS.WebAPI.Providers.Blockchain.Solana;
using OASIS.WebAPI.Core.Blockchain.Wormhole;
using OASIS.WebAPI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddAutoMapper(typeof(Program).Assembly);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "OASIS WebAPI", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\""
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    var key = builder.Configuration.GetValue<string>("Jwt:Key") ?? throw new InvalidOperationException("JWT Key is missing.");
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))
    };
});

builder.Services.AddAuthorization();
builder.Services.AddSingleton<IProviderHealthMonitor, ProviderHealthMonitor>();
builder.Services.AddSingleton<IProviderSelectionStrategy, HealthScoreStrategy>();
builder.Services.AddScoped<ProviderContext>(sp =>
{
    var providers = sp.GetRequiredService<IEnumerable<IOASISStorageProvider>>();
    var config = sp.GetRequiredService<IConfiguration>();
    var healthMonitor = sp.GetService<IProviderHealthMonitor>();
    var customStrategyName = config.GetValue<string>("OASIS:CustomProviderStrategy");

    IProviderSelectionStrategy? customStrategy = customStrategyName?.ToLowerInvariant() switch
    {
        "weighted" => new WeightedStrategy(config),
        "sticky-session" => new StickySessionStrategy(),
        _ => null
    };

    return new ProviderContext(providers, config, healthMonitor, customStrategy);
});

builder.Services.AddScoped<IAvatarManager, AvatarManager>();
builder.Services.AddScoped<IWalletManager, WalletManager>();
builder.Services.AddScoped<IHolonManager, HolonManager>();
builder.Services.AddScoped<IBlockchainOperationManager, BlockchainOperationManager>();
builder.Services.AddScoped<ISTARManager, STARManager>();
builder.Services.AddScoped<INftManager, NftManager>();
builder.Services.AddScoped<ISearchManager, SearchManager>();
builder.Services.AddScoped<IAvatarNFTService, AvatarNFTService>();

var connectionString = builder.Configuration.GetConnectionString("OASISDatabase")
    ?? throw new InvalidOperationException("Connection string 'OASISDatabase' not found.");

builder.Services.AddDbContext<OASISDbContext>(options =>
    options.UseNpgsql(connectionString, b => b.MigrationsAssembly("OASIS.WebAPI")));

builder.Services.AddScoped<IOASISStorageProvider, EfStorageProvider>();
builder.Services.AddSingleton<IOASISStorageProvider, InMemoryStorageProvider>();

// Wrap all resolved providers with health-recording decorator
builder.Services.AddScoped<IEnumerable<IOASISStorageProvider>>(sp =>
{
    var healthMonitor = sp.GetRequiredService<IProviderHealthMonitor>();
    var rawProviders = sp.GetServices<IOASISStorageProvider>();
    return rawProviders.Select(p => new HealthRecordingProviderDecorator(p, healthMonitor)).ToList();
});

// ─── Blockchain providers & factory ───
builder.Services.AddSingleton<IBlockchainProvider, AlgorandProvider>();
builder.Services.AddSingleton<IBlockchainProvider, SolanaProvider>();
builder.Services.AddSingleton<IBlockchainProviderFactory>(sp =>
{
    var registeredProviders = sp.GetRequiredService<IEnumerable<IBlockchainProvider>>();
    return new BlockchainProviderFactory(registeredProviders, sp.GetRequiredService<IConfiguration>());
});

// ─── Wormhole trustless bridge adapter ───
builder.Services.Configure<WormholeConfig>(
    builder.Configuration.GetSection(WormholeConfig.SectionName));
builder.Services.AddHttpClient<IWormholeAdapter, WormholeAdapter>((sp, client) =>
{
    var config = builder.Configuration
        .GetSection(WormholeConfig.SectionName)
        .Get<WormholeConfig>() ?? new WormholeConfig();
    client.BaseAddress = new Uri(config.GuardianRpcUrl);
    client.Timeout = TimeSpan.FromSeconds(config.VaaTimeoutSeconds + 10);
});

// ─── Cross-chain bridge (hybrid trusted + Wormhole) ───
builder.Services.AddSingleton<ICrossChainBridgeService, CrossChainBridgeService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Dev", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
    db.Database.EnsureCreated();
}

app.UseHttpsRedirection();
app.UseCors("Dev");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { }
