using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Reflection;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Core.Blockchain;
using OASIS.WebAPI.Extensions;
using OASIS.WebAPI.Core.Blockchain.Wormhole;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Managers;
using OASIS.WebAPI.Interfaces.QuestExecution;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Managers.Dex;
using OASIS.WebAPI.Models.Responses;
using FluentValidation;
using FluentValidation.AspNetCore;
using OASIS.WebAPI.Observability;
using OASIS.WebAPI.Providers.Blockchain.Algorand;
using OASIS.WebAPI.Providers.Blockchain.Solana;
using OASIS.WebAPI.Providers.Stores;
using OASIS.WebAPI.Services;
using OASIS.WebAPI.Services.Quest;
using OASIS.WebAPI.Services.Quest.Handlers;
using OASIS.WebAPI.Services.Wormhole;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
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
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "X-Api-Key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "API Key authentication. Example: \"oasis_abc123...\""
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        },
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "MultiScheme";
    options.DefaultChallengeScheme = "MultiScheme";
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
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
})
.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationHandler.SchemeName, _ => { })
.AddPolicyScheme("MultiScheme", "JWT or API Key", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        if (context.Request.Headers.ContainsKey("X-Api-Key"))
            return ApiKeyAuthenticationHandler.SchemeName;
        return JwtBearerDefaults.AuthenticationScheme;
    };
});

builder.Services.AddAuthorization();

// ─── Rate limiting + per-API-key metering (api-safety-hardening task 18) ───
// Built-in ASP.NET Core 8 rate limiting (Microsoft.AspNetCore.RateLimiting —
// part of the Web shared framework, NO NuGet package required).
//
// Partition strategy (most-specific identity wins, so each principal is
// metered independently):
//   1. X-Api-Key header present  -> partition per API key  (per-key metering)
//   2. else authenticated avatar -> partition per avatar/user id
//   3. else anonymous            -> partition per client IP
//
// Two limiters:
//   • a permissive GLOBAL limiter applied to every endpoint, and
//   • a STRICT named policy ("financial") attached to the irreversible /
//     value-moving endpoints (bridge initiate/redeem/reverse, swap execute,
//     wallet transfer, wallet topup) via [EnableRateLimiting("financial")].
//
// All limits are config-overridable from the "RateLimiting" section
// (config-driven preference); the literals below are conservative fallbacks.
var rlSection = builder.Configuration.GetSection("RateLimiting");
var rlEnabled = rlSection.GetValue<bool?>("Enabled") ?? true;
var rlGlobalPermit = rlSection.GetValue<int?>("Global:PermitLimit") ?? 120;
var rlGlobalWindowSeconds = rlSection.GetValue<int?>("Global:WindowSeconds") ?? 60;
var rlGlobalQueue = rlSection.GetValue<int?>("Global:QueueLimit") ?? 0;
var rlFinancialPermit = rlSection.GetValue<int?>("Financial:PermitLimit") ?? 10;
var rlFinancialWindowSeconds = rlSection.GetValue<int?>("Financial:WindowSeconds") ?? 60;
var rlFinancialQueue = rlSection.GetValue<int?>("Financial:QueueLimit") ?? 0;

// Stable partition key for the *current* request: API key, else avatar/user,
// else IP. Prefixed so the three identity spaces never collide.
static string ResolveRateLimitPartitionKey(HttpContext httpContext)
{
    if (httpContext.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues))
    {
        var apiKey = apiKeyValues.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // Hash so the raw secret never lives in limiter state / logs, but
            // the partition is still 1:1 with the key (per-API-key metering).
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(apiKey)));
            return $"apikey:{hash}";
        }
    }

    var user = httpContext.User;
    if (user?.Identity?.IsAuthenticated == true)
    {
        var sub = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? user.FindFirst("sub")?.Value
                  ?? user.FindFirst("AvatarId")?.Value;
        if (!string.IsNullOrWhiteSpace(sub))
            return $"avatar:{sub}";
    }

    var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return $"ip:{ip}";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global limiter — partitioned by identity (api key / avatar / ip).
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        if (!rlEnabled)
            return RateLimitPartition.GetNoLimiter("disabled");

        var key = ResolveRateLimitPartitionKey(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rlGlobalPermit,
            Window = TimeSpan.FromSeconds(rlGlobalWindowSeconds),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = rlGlobalQueue
        });
    });

    // Strict named policy for irreversible / value-moving endpoints.
    options.AddPolicy("financial", httpContext =>
    {
        if (!rlEnabled)
            return RateLimitPartition.GetNoLimiter("disabled");

        var key = ResolveRateLimitPartitionKey(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter($"fin:{key}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rlFinancialPermit,
            Window = TimeSpan.FromSeconds(rlFinancialWindowSeconds),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = rlFinancialQueue
        });
    });

    options.OnRejected = (context, _) =>
    {
        // Emit a Retry-After that matches the policy that actually rejected
        // the request. Prefer the limiter lease's own RetryAfter metadata;
        // otherwise fall back to the matched policy's window (the stricter
        // "financial" policy has its own, shorter-quota window — reporting the
        // permissive global window there would mislead the client).
        int retryAfterSeconds;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra))
        {
            retryAfterSeconds = (int)Math.Ceiling(ra.TotalSeconds);
        }
        else
        {
            var matchedFinancial = context.HttpContext.GetEndpoint()?
                .Metadata.GetMetadata<EnableRateLimitingAttribute>()?
                .PolicyName == "financial";
            retryAfterSeconds = matchedFinancial ? rlFinancialWindowSeconds : rlGlobalWindowSeconds;
        }

        context.HttpContext.Response.Headers["Retry-After"] =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
});

// Retained for /health (StorageHealthCheck / ProviderHealthMonitorHealthCheck).
// The provider-selection + decorator + routing layer was deleted in Mission B
// (single-provider reality); managers now inject concrete per-aggregate EF
// stores directly.
builder.Services.AddSingleton<IProviderHealthMonitor, ProviderHealthMonitor>();

// ─── Per-aggregate EF stores (replace the deleted god storage seam) ───
// Scoped to match OASISDbContext lifetime. Adding a new aggregate = add one
// I*Store + Ef*Store pair and one line here.
builder.Services.AddScoped<IAvatarStore, EfAvatarStore>();
builder.Services.AddScoped<IWalletStore, EfWalletStore>();
builder.Services.AddScoped<IHolonStore, EfHolonStore>();
builder.Services.AddScoped<IBlockchainOperationStore, EfBlockchainOperationStore>();
builder.Services.AddScoped<ISTARStore, EfStarStore>();
builder.Services.AddScoped<IQuestStore, EfQuestStore>();
builder.Services.AddScoped<INftStore, EfNftStore>();
builder.Services.AddScoped<IBridgeStore, EfBridgeStore>();

// <quest-temporal-fork-model>
// Per-attempt runtime stores for QuestRun + QuestNodeExecution. InMemory is
// the default during the transition window; the SurrealDB-backed adapter
// arrives with surrealdb-migration tasks 9–10 (see SURREAL-SCHEMA-HINTS.md).
// Singleton is appropriate for InMemory (process-lifetime state); EF stubs
// (EfQuestRunStore/EfQuestNodeExecutionStore) are [Obsolete] and not wired.
builder.Services.AddSingleton<IQuestRunStore, InMemoryQuestRunStore>();
builder.Services.AddSingleton<IQuestNodeExecutionStore, InMemoryQuestNodeExecutionStore>();
// </quest-temporal-fork-model>

// <surrealdb-client-package>
// Homebake SurrealDB client (Phase 6, sub-wave 1.5a). Replaces direct
// registration of SurrealDb.Net's ISurrealDbClient. Binds
// SurrealConnectionOptions from the "SurrealDb" configuration section by
// default (override the section name via the optional argument). The actual
// SurrealDB-backed *Store adapters land in surrealdb-migration wave-2 tasks
// 5-8; until then this registration just makes the client available for
// any code that wants to use it (integration tests, future adapters).
builder.Services.AddOasisSurrealDb(builder.Configuration);
// </surrealdb-client-package>

// SwapManager's quote cache is now an injected, bounded IMemoryCache (was a
// process-static dictionary). SizeLimit is required because every cache write
// sets per-entry Size=1; without a limit the SetSize call throws.
builder.Services.AddMemoryCache(o => o.SizeLimit = 1024);

builder.Services.AddScoped<IAvatarManager, AvatarManager>();
builder.Services.AddSingleton<WalletKeyService>();
builder.Services.AddSingleton<IAlgorandFaucet, AlgorandFaucet>();
builder.Services.AddScoped<IWalletManager, WalletManager>();
builder.Services.AddScoped<IHolonManager, HolonManager>();
// Bind Jupiter DEX configuration
builder.Services.Configure<JupiterConfig>(
    builder.Configuration.GetSection(JupiterConfig.SectionName));

// DEX adapters — one IDexAdapter per chain. Adding a new chain = add one
// IDexAdapter implementation + one registration line here; SwapManager (the
// dispatcher) never changes. The Jupiter adapter uses a typed HttpClient that
// preserves the prior Jupiter HttpClient config (15s timeout + User-Agent)
// that previously lived on AddHttpClient<ISwapManager, SwapManager>.
builder.Services.AddHttpClient<JupiterDexAdapter>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Add("User-Agent", "OASIS-SwapManager/1.0");
});
builder.Services.AddScoped<IDexAdapter>(sp => sp.GetRequiredService<JupiterDexAdapter>());
// Tinyman creates its own short-lived Algod HttpClient internally (no typed client needed).
builder.Services.AddScoped<IDexAdapter, TinymanDexAdapter>();
builder.Services.AddScoped<ISwapManager, SwapManager>();
builder.Services.AddScoped<IBlockchainOperationManager, BlockchainOperationManager>();
builder.Services.AddScoped<ISTARManager, STARManager>();
builder.Services.AddScoped<INftManager, NftManager>();
builder.Services.AddScoped<ISearchManager, SearchManager>();
builder.Services.AddScoped<IAvatarNFTService, AvatarNFTService>();
builder.Services.AddScoped<IQuestManager, QuestManager>();

var connectionString = builder.Configuration.GetConnectionString("OASISDatabase")
    ?? throw new InvalidOperationException("Connection string 'OASISDatabase' not found.");

builder.Services.AddDbContext<OASISDbContext>(options =>
    options.UseNpgsql(connectionString, b => b.MigrationsAssembly("OASIS.WebAPI")));

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

// Real secp256k1 ecrecover Guardian-signature verifier. Once registered,
// WormholeAdapter.VerifyVAAAsync performs genuine per-signature verification
// against the config-driven Guardian set. RequireFullSignatureVerification
// stays true (default) — the "no verifier ⇒ fail-closed" path is unchanged
// and still exercised by adapter tests that build it without a verifier.
builder.Services.AddScoped<IVaaSignatureVerifier, Secp256k1VaaSignatureVerifier>();

// ─── Idempotency store (api-safety-hardening task 9/10/11/12) ───
// REQUIRED: CrossChainBridgeService & BlockchainOperationManager take
// IIdempotencyStore as a ctor dependency; AlgorandFaucet resolves it per-call
// via IServiceScopeFactory. Scoped — matches OASISDbContext lifetime.
builder.Services.AddScoped<OASIS.WebAPI.Interfaces.IIdempotencyStore,
    OASIS.WebAPI.Core.Idempotency.IdempotencyStore>();

// ─── Cross-chain bridge (hybrid trusted + Wormhole) — Scoped for EF DbContext access ───
builder.Services.AddScoped<ICrossChainBridgeService, CrossChainBridgeService>();

// ─── Chain reconciliation (api-safety-hardening tasks 14/15) ───
// Scoped service re-derives true status from chain truth; the hosted service
// drives a periodic sweep, creating a DI scope per tick.
builder.Services.AddOptions<OASIS.WebAPI.Services.Reconciliation.ReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(
        OASIS.WebAPI.Services.Reconciliation.ReconciliationOptions.SectionName));
builder.Services.AddScoped<OASIS.WebAPI.Interfaces.IReconciliationService,
    OASIS.WebAPI.Services.Reconciliation.ReconciliationService>();
builder.Services.AddHostedService<OASIS.WebAPI.Services.Reconciliation.ReconciliationHostedService>();

// ─── Durable saga / transactional outbox (durable-saga-orchestration Phase 1) ───
// Generic, reusable, bridge-agnostic. Mirrors the reconciliation registrations:
// options-bound section, scoped store/processor (per-tick DI scope), a hosted
// processor driven by a swappable polling trigger (SurrealDB LIVE-query later).
builder.Services.AddOptions<OASIS.WebAPI.Sagas.SagaOptions>()
    .Bind(builder.Configuration.GetSection(
        OASIS.WebAPI.Sagas.SagaOptions.SectionName));
builder.Services.AddScoped<OASIS.WebAPI.Sagas.ISagaStore,
    OASIS.WebAPI.Sagas.EfSagaStore>();
builder.Services.AddSingleton<OASIS.WebAPI.Sagas.ISagaRegistry,
    OASIS.WebAPI.Sagas.SagaRegistry>();
builder.Services.AddScoped<OASIS.WebAPI.Sagas.ISagaCoordinator,
    OASIS.WebAPI.Sagas.SagaCoordinator>();
builder.Services.AddScoped<OASIS.WebAPI.Sagas.ISagaProcessor,
    OASIS.WebAPI.Sagas.SagaProcessor>();
builder.Services.AddSingleton<OASIS.WebAPI.Sagas.ISagaTrigger,
    OASIS.WebAPI.Sagas.PollingSagaTrigger>();
builder.Services.AddHostedService<OASIS.WebAPI.Sagas.SagaProcessorHostedService>();

// ─── Quest DAG system ───
builder.Services.AddScoped<OASIS.WebAPI.Interfaces.IQuestDagValidator, OASIS.WebAPI.Services.QuestDagValidator>();
builder.Services.AddScoped<OASIS.WebAPI.Interfaces.IQuestInstantiator, OASIS.WebAPI.Services.Quest.QuestInstantiator>();

// Quest node handlers — exactly one per QuestNodeType. Registered Scoped: each
// handler wraps scoped managers, so Singleton would capture a disposed scope
// (captive-dependency bug). Discovered by assembly scan over the single sealed
// Services/Quest/Handlers namespace so adding a handler needs no DI edit; the
// registry's ctor still fails fast on a duplicate/missing QuestNodeType.
foreach (var handlerType in typeof(QuestNodeHandlerRegistry).Assembly
             .GetTypes()
             .Where(t => t is { IsClass: true, IsAbstract: false }
                         && t.Namespace == "OASIS.WebAPI.Services.Quest.Handlers"
                         && typeof(IQuestNodeHandler).IsAssignableFrom(t)))
{
    builder.Services.AddScoped(typeof(IQuestNodeHandler), handlerType);
}
builder.Services.AddScoped<IQuestNodeHandlerRegistry, QuestNodeHandlerRegistry>();

// ─── Observability (W5): OpenTelemetry tracing/metrics + /health ───
builder.Services.AddOasisObservability(builder.Configuration);
builder.Services.AddOasisHealthChecks();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Dev", policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

// Verbose error reporting.
//   • Opt-in via OASIS:DebugErrors (env: OASIS__DebugErrors); defaults on in Development.
//   • HARD GUARDRAIL: Production can NEVER emit verbose debug, no matter what
//     config or environment variables say. Only platform devs running a
//     non-Production environment can turn this on — so stack traces and other
//     internals can never leak from a production deployment.
var debugRequested = builder.Configuration.GetValue<bool?>("OASIS:DebugErrors")
    ?? app.Environment.IsDevelopment();
OASISResultDebug.Enabled = debugRequested && !app.Environment.IsProduction();

app.Logger.LogInformation(
    "Verbose error debug is {State} (environment={Environment}, requested={Requested}).",
    OASISResultDebug.Enabled ? "ENABLED" : "disabled",
    app.Environment.EnvironmentName,
    debugRequested);
if (debugRequested && app.Environment.IsProduction())
    app.Logger.LogWarning(
        "OASIS:DebugErrors was requested but is FORCE-DISABLED in Production.");

// Must be the first middleware so it wraps the entire pipeline and turns any
// unhandled throw into a structured (debug-aware) JSON error instead of a
// blank HTTP 500.
app.UseMiddleware<DebugExceptionMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Auto-migrate database on startup (creates DB + applies all pending migrations)
// Then seed demo data if the database is empty
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OASISDbContext>();
    db.Database.Migrate();
    await SeedData.SeedAsync(db);
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("Dev");
app.UseAuthentication();
app.UseAuthorization();
// Rate limiter AFTER auth so the partition key can fall back to the
// authenticated avatar/user when no X-Api-Key header is present.
app.UseRateLimiter();
// W5 request correlation: after UseRouting, before MapControllers — attaches
// the W3C TraceId/SpanId as a structured log scope for every request.
app.UseOasisRequestCorrelation();
// ISwapManager + IDexAdapter registrations are above (DEX adapters block)
app.MapControllers();
app.MapOasisHealth();
await app.RunAsync();

public partial class Program { }
