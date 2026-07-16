using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using AZOA.WebAPI.Core.Networking;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Reflection;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Core.Blockchain;
using AZOA.WebAPI.Extensions;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Middleware;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Avatar;
using AZOA.WebAPI.Services.Blockchain;
using AZOA.WebAPI.Services.Dex;
using AZOA.WebAPI.Services.Signing;
using AZOA.WebAPI.Models.Responses;
using FluentValidation;
using FluentValidation.AspNetCore;
using AZOA.WebAPI.Observability;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Mcp;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Services.Wormhole;
using AZOA.WebAPI.Services.Conformance;

var builder = WebApplication.CreateBuilder(args);

// Optional JSONL diagnostic sink; each environment must enable it explicitly.
builder.AddJsonlExceptionLogging();

builder.Services.AddControllers(options =>
    {
        // Fail closed on a missing/malformed [FromBody] payload with a uniform 400
        // instead of letting the action dereference a null body (→ opaque 500).
        options.Filters.Add<AZOA.WebAPI.Core.Filters.RequireRequestBodyFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
var dataProtectionApplicationName = builder.Configuration["DataProtection:ApplicationName"]
    ?? "AZOA.WebAPI.NodeTransparency.v1";
var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName(dataProtectionApplicationName);
var dataProtectionKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeyRingPath))
{
    dataProtection.PersistKeysToFileSystem(
        new DirectoryInfo(Path.GetFullPath(dataProtectionKeyRingPath)));
}
else if (!builder.Environment.IsDevelopment()
    && !builder.Environment.IsEnvironment("IntegrationTest"))
{
    throw new InvalidOperationException(
        "DataProtection:KeyRingPath is required outside Development/IntegrationTest so opaque public cursors " +
        "remain valid across restarts and instances. Mount the configured directory on durable shared storage.");
}
builder.Services.AddOptions<NodeConformanceOptions>()
    .Bind(builder.Configuration.GetSection(NodeConformanceOptions.SectionName));
builder.Services.AddOptions<AZOA.WebAPI.Services.Governance.NodeTransparencyHistoryOptions>()
    .Bind(builder.Configuration.GetSection(AZOA.WebAPI.Services.Governance.NodeTransparencyHistoryOptions.SectionName));
builder.Services.AddSingleton<INodeConformanceEvidenceSource, TrxNodeConformanceEvidenceSource>();
builder.Services.AddSingleton<INodeIdentityKeyService, ProtectedFileNodeIdentityKeyService>();
builder.Services.AddSingleton<INodeConformanceManifestService, NodeConformanceManifestService>();
builder.Services.AddSingleton<AZOA.WebAPI.Services.Governance.NodeTransparencyHistoryCheckpointStore>();
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
// AutoMapper 15.x: AddAutoMapper takes a config action; AddMaps scans the assembly
// for Profile types (was the removed AddAutoMapper(Assembly) overload).
builder.Services.AddAutoMapper(cfg => cfg.AddMaps(typeof(Program).Assembly));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "AZOA WebAPI", Version = "v1" });
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
        Description = "API Key authentication. Example: \"azoa_abc123...\""
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

// Fail-fast secret guards: outside Development/IntegrationTest, the JWT signing
// key and the wallet encryption key MUST be supplied from the environment and
// must not be the committed placeholders. A missing or placeholder secret in
// Production is a hard startup failure, not a silent fallback.
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("IntegrationTest"))
{
    const string jwtPlaceholder = "your-super-secret-key-min-32-chars!!";
    const string walletPlaceholder = "azoa-wallet-encryption-key-change-in-production!";

    var jwtKey = builder.Configuration.GetValue<string>("Jwt:Key");
    if (string.IsNullOrEmpty(jwtKey) || jwtKey == jwtPlaceholder)
        throw new InvalidOperationException(
            "Jwt:Key is missing or set to the committed placeholder. Set a strong " +
            "(>=32 char) secret via the Jwt__Key environment variable before starting " +
            "outside Development.");

    var walletKey = builder.Configuration.GetValue<string>("AZOA:WalletEncryptionKey");
    if (string.IsNullOrEmpty(walletKey) || walletKey == walletPlaceholder)
        throw new InvalidOperationException(
            "AZOA:WalletEncryptionKey is missing or set to the committed placeholder. " +
            "Set a strong secret via the AZOA__WalletEncryptionKey environment variable " +
            "before starting outside Development.");
}

SurrealRuntimeConfigurationGuard.GuardProduction(
    builder.Configuration, builder.Environment.IsProduction());
var surrealRuntimeConfigSection = SurrealRuntimeConfigurationGuard.ResolveRuntimeSectionName(
    builder.Configuration, builder.Environment.IsProduction());

// Fail-fast simulated-mode guard (H1): Blockchain:Mode=Simulated short-circuits
// every chain to fake sim:tx: settlement with no real network I/O. That must
// never happen in Production; Dev/IntegrationTest/other envs are unaffected.
AZOA.WebAPI.Providers.Blockchain.BlockchainProviderFactory.GuardAgainstSimulatedModeInProduction(
    builder.Configuration, builder.Environment.IsProduction());

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
        IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key))
    };

    // user-sovereign-identity AC3b (security-review fix): the per-avatar
    // AuthNotBefore watermark MUST be re-checked on EVERY request, not only when a
    // child credential is minted. ValidateLifetime alone checks the token's OWN nbf
    // against now — it has no knowledge of the avatar's watermark. Without this, a
    // tenant child JWT minted seconds before the user claims their avatar stays valid
    // to its natural exp (up to 15 min) AFTER the user severed custody. This event
    // loads the subject avatar and FAILS the token when it was issued before the
    // current watermark. Fail-closed: a lookup error rejects the token.
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var principal = context.Principal;
            var sub = principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                      ?? principal?.FindFirst("sub")?.Value;
            if (!Guid.TryParse(sub, out var avatarId))
            {
                // No resolvable subject — nothing avatar-scoped to enforce; the rest
                // of validation already passed. (Non-avatar principals are rare here.)
                return;
            }

            var avatarStore = context.HttpContext.RequestServices
                .GetRequiredService<AZOA.WebAPI.Interfaces.Stores.IAvatarStore>();
            var loaded = await avatarStore.GetByIdAsync(avatarId, context.HttpContext.RequestAborted);
            if (loaded.IsError || loaded.Result is null)
            {
                // Fail-closed: a token whose subject avatar cannot be loaded is rejected.
                context.Fail("Subject avatar could not be verified.");
                return;
            }

            var watermark = loaded.Result.AuthNotBefore;
            if (watermark is null)
                return; // never claimed / no watermark — nothing to enforce.

            // The token's issued-at / not-before must be at/after the watermark. A
            // token minted before the avatar's last claim is stale and rejected,
            // closing the post-claim residual-credential window. ValidFrom surfaces the
            // token's nbf (falling back to iat) as a UTC DateTime; if neither is present
            // ValidFrom is DateTime.MinValue, which is always stale against a watermark.
            var jwt = context.SecurityToken as System.IdentityModel.Tokens.Jwt.JwtSecurityToken;
            // Payload.IssuedAt surfaces the token's iat as a UTC DateTime (DateTime.MinValue
            // when absent), superseding the deprecated int? Payload.Iat. Fall back to nbf
            // (ValidFrom) when iat is unset so a nbf-only token still watermarks correctly.
            var issuedAt = jwt?.Payload?.IssuedAt ?? DateTime.MinValue;
            var tokenInstant = issuedAt != DateTime.MinValue
                ? issuedAt
                : (jwt?.ValidFrom ?? DateTime.MinValue);

            if (AZOA.WebAPI.Core.AuthWatermark.IsTokenStale(tokenInstant, watermark))
                context.Fail("Token was issued before the avatar's authentication watermark (post-claim cut).");
        }
    };
})
.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
    ApiKeyAuthenticationHandler.SchemeName, _ => { })
.AddScheme<AuthenticationSchemeOptions, CredentialFreeAuthenticationHandler>(
    CredentialFreeAuthenticationHandler.SchemeName, _ => { })
.AddPolicyScheme("MultiScheme", "JWT, API Key, or credential-free public", options =>
{
    options.ForwardDefaultSelector = AuthenticationSchemeSelector.Resolve;
});

// TenantScope policy (tenant-onboarding): a tenant-surface action requires the
// tenant:provision scope claim (emitted per-CSV-entry by ApiKeyAuthenticationHandler).
//
// Operator policy (security-review HIGH-2): gates the MOST destructive surfaces in the
// tree — live wrapping-key rotation and data backfill, both of which rewrite state
// across every avatar. Hardened along two independent axes so no single misconfig
// grants admin:
//   1. AUTHENTICATION-SCHEME FLOOR. The principal must NOT have been authenticated via
//      the API-key scheme. ApiKeyAuthenticationHandler stamps AuthMethod=ApiKey; any
//      principal carrying that claim is rejected outright. This means an X-Api-Key —
//      which can only ever emit `scope` claims from its stored CSV, never a JWT role —
//      can never reach these endpoints even if it somehow carried an admin-looking
//      claim. (Belt: ApiKeyAuthenticationHandler also strips the operator scope at
//      emit time; suspenders: this policy refuses the whole scheme.)
//   2. EXPLICIT OPERATOR CAPABILITY. Beyond the scheme floor, the JWT must carry a real
//      admin signal: the dedicated operator:admin scope (minted only for admins), the
//      "Admin" role, a role=Admin claim, or is_admin=true — the KycController.IsAdmin
//      convention, now additionally fenced behind the scheme floor.
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("TenantScope", p =>
        p.RequireAssertion(ctx =>
            ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.TenantProvision)));
    // DappDevelop gates authoring writes; DappManage gates lifecycle writes.
    // API keys inherit only the owner's current dApp role and, when scoped, must
    // also carry the matching explicit dapp capability.
    o.AddPolicy("DappDevelop", p =>
        p.RequireAssertion(ctx =>
        {
            var authMethod = ctx.User.FindFirst("AuthMethod")?.Value;
            var isApiKey = string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase);

            // Not an API key (i.e. a JWT identity) → must prove a dapp capability.
            if (!isApiKey)
                return ctx.User.HasDappDevelopAccess();

            // CSV was non-empty but every token was dropped as forbidden (all-forbidden
            // scope list) → must NOT be treated as legacy full-access. Deny.
            if (string.Equals(ctx.User.FindFirst("ScopesRestricted")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            // Genuinely-empty-CSV key emits no scope claims → legacy full-access → allowed.
            if (ctx.User.GetScopes().Count == 0)
                return ctx.User.HasDappDeveloperRole();

            // Scoped key → must explicitly carry the dapp-developer capability.
            return ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.DappDevelop)
                && ctx.User.HasDappDeveloperRole();
        }));
    o.AddPolicy("DappManage", p =>
        p.RequireAssertion(ctx =>
        {
            var authMethod = ctx.User.FindFirst("AuthMethod")?.Value;
            var isApiKey = string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase);

            if (!isApiKey)
                return ctx.User.HasDappManageAccess();

            if (string.Equals(ctx.User.FindFirst("ScopesRestricted")?.Value, "true", StringComparison.OrdinalIgnoreCase))
                return false;

            if (ctx.User.GetScopes().Count == 0)
                return ctx.User.HasDappManagerRole();

            return ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.DappManage)
                && ctx.User.HasDappManagerRole();
        }));
    o.AddPolicy("Operator", p =>
        p.RequireAssertion(ctx =>
        {
            // Axis 1: reject API-key-authenticated principals outright. An API key can
            // never be an operator — operator authority originates only from a JWT.
            var authMethod = ctx.User.FindFirst("AuthMethod")?.Value;
            if (string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase))
                return false;

            // Axis 2: require an explicit admin/operator capability on the JWT.
            return ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.Operator)
                || ctx.User.IsInRole("Admin")
                || string.Equals(ctx.User.FindFirst("role")?.Value, "Admin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ctx.User.FindFirst("is_admin")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        }));
    o.AddPolicy("NodeGovern", p =>
        p.RequireAssertion(ctx =>
        {
            var authMethod = ctx.User.FindFirst("AuthMethod")?.Value;
            if (string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase))
                return false;

            return ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.NodeGovern);
        }));
});

// Actionable authorization failures (DX audit): a denied scoped API key gets a JSON
// body naming the missing dapp:develop scope instead of a bare 403. The interface
// lives in Microsoft.AspNetCore.Authorization (NOT the .Policy sub-namespace).
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
    AZOA.WebAPI.Services.Auth.ActionableAuthorizationResultHandler>();

// ─── Rate limiting + per-API-key metering (api-safety-hardening task 18) ───
// Built-in ASP.NET Core 8 rate limiting (Microsoft.AspNetCore.RateLimiting —
// part of the Web shared framework, NO NuGet package required).
//
// Partition strategy (most-specific identity wins, so each principal is
// metered independently):
//   1. authenticated API key     -> partition per server-issued key id
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

var rlDevMultiplier = builder.Environment.IsDevelopment()
    ? Math.Max(1, rlSection.GetValue<int?>("DevMultiplier") ?? 1)
    : 1;
rlGlobalPermit    *= rlDevMultiplier;
rlFinancialPermit *= rlDevMultiplier;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global limiter — partitioned by identity (api key / avatar / ip).
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        if (!rlEnabled)
            return RateLimitPartition.GetNoLimiter("disabled");

        var key = RateLimitPartitionKey.Resolve(httpContext);
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

        var key = RateLimitPartitionKey.Resolve(httpContext);
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

// ─── Per-aggregate stores ───
// surrealdb-migration wave-2 close-out (Stream A): Avatar/Holon/STAR flip to
// SurrealDB now that their 090/100/110 .mermaid/.surql schemas + inline-POCO
// adapters have landed. Only Quest remains on EF, gated on
// quest-temporal-fork-model. Scoped lifetime in both cases.
builder.Services.AddScoped<IAvatarStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealAvatarStore>();
builder.Services.AddScoped<IWalletStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealWalletStore>();
builder.Services.AddScoped<IHolonStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonStore>();
// final-hardening-cutover F5: opt-in Holon AssetType registry store.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IHolonTypeRegistryStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealHolonTypeRegistryStore>();
// node-operator-governance: singleton local node parameters + immutable audit.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.INodeGovernanceStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNodeGovernanceStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.INodeFeeScheduleStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNodeFeeScheduleStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.INodeTreasuryStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNodeTreasuryStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.INodeTransparencyStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNodeTransparencyStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IAdminBootstrapStateStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealAdminBootstrapStateStore>();
builder.Services.AddScoped<IBlockchainOperationStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealBlockchainOperationStore>();
builder.Services.AddScoped<ISTARStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealStarStore>();
// star-odk-ecosystem-tree (D2): tree of DappSeries/STARODK nodes owned by a STARODK.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IEcosystemStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealEcosystemStore>();
// kyc-module: KYC submission/document persistence (SurrealDB).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IKycStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealKycStore>();
// surrealdb-migration wave-2 round-3 close (residual task 9): IQuestStore
// flips to the SurrealDB-backed adapter now that the definition-side
// schema files (150_quest / 160_quest_node / 170_quest_edge) and the
// existing quest_template / quest_node_template tables (130 / 140) are
// all in place — quests now survive restart. The InMemoryQuestStore
// class file remains on disk as a one-line revert path; only the DI
// binding is flipped here. Definition-side template reads continue to
// be served by SurrealQuestTemplateStore via the separate
// IQuestTemplateStore interface (CLOSEOUT Stream C2) — both shapes
// share the underlying quest_template row format.
builder.Services.AddScoped<IQuestStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestStore>();
builder.Services.AddScoped<INftStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNftStore>();
builder.Services.AddScoped<IBridgeStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealBridgeStore>();
// surrealdb-migration CLOSEOUT Stream C2 (pre-D gap closure): ApiKey + Quest
// template catalog flipped to per-aggregate Surreal stores. ApiKey: backs
// ApiKeyAuthenticationHandler + ApiKeyController. QuestTemplate: backs
// QuestInstantiator (definition-side catalog reads only — the runtime
// quest_run / quest_node_execution tables are owned by
// quest-temporal-fork-model and remain on the InMemory adapter until that
// track lands).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IApiKeyStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealApiKeyStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IQuestTemplateStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestTemplateStore>();

// ─── Data-backfill primitive (final-hardening-cutover Phase E2) ───
// Application-level data-rewrite runner + data_migration ledger. Distinct from
// surrealforge's schema_migration DDL ledger. IBackfill units are registered as
// scoped services (empty registry today — greenfield pre-launch has zero rows to
// rewrite); the runner discovers them via IEnumerable<IBackfill>. Surface:
// GET/POST /api/admin/backfill (Operator policy). See Services/Backfill/AGENTS.md.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IDataMigrationLedgerStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealDataMigrationLedgerStore>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Backfill.BackfillRunner>();

// <quest-temporal-fork-model>
// Per-attempt runtime stores for QuestRun + QuestNodeExecution. As of
// surrealdb-migration wave-2 round-3 close (residual task 9) both flip to
// the SurrealDB-backed adapters — quests + per-(run, node) execution rows
// now survive restart. The schemas they bind to are
// 190_quest_run.surql + 200_quest_node_execution.surql, with the
// 230_quest_graph_edges.surql RELATION tables (forked_from + executes)
// providing the cheap-graph-traversal lane for lineage walks. The
// InMemory* class files remain on disk so a future revert is a one-line
// DI swap.
builder.Services.AddScoped<IQuestRunStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestRunStore>();
builder.Services.AddScoped<IQuestNodeExecutionStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestNodeExecutionStore>();
// </quest-temporal-fork-model>

// quest-invitations-approval: request/approval flow store for invite-gated quests.
builder.Services.AddScoped<IQuestAccessRequestStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestAccessRequestStore>();

// ─── user-self-sovereignty: consent + wallet-auth + webhook stores ───────────
// user-sovereign-identity: wallet-challenge auth nonce + tenant claim-invite token.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWalletAuthChallengeStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealWalletAuthChallengeStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWalletAuthClaimTokenStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealWalletAuthClaimTokenStore>();
// tenant-consent-delegation: the live grant store (the seam's source of truth),
// the immutable audit trail (AC10), and the webhook outbox + registration (AC7).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IConsentGrantStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealConsentGrantStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IConsentAuditStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealConsentAuditStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IConsentWebhookOutboxStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealConsentWebhookOutboxStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWebhookRegistrationStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealWebhookRegistrationStore>();
// final-hardening F3: generic quest.emit webhook transactional outbox — a parallel
// outbox to the consent one that SHARES the registration store (above) + SSRF guard +
// HMAC signer + WebhookOptions (all registered on the consent path below).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IQuestWebhookOutboxStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealQuestWebhookOutboxStore>();

// <surrealdb-client-package>
// Homebake SurrealDB client (Phase 6, sub-wave 1.5a). Replaces direct
// registration of SurrealDb.Net's ISurrealDbClient. Binds
// SurrealConnectionOptions from the isolated runtime configuration section. The actual
// SurrealDB-backed *Store adapters land in surrealdb-migration wave-2 tasks
// 5-8; until then this registration just makes the client available for
// any code that wants to use it (integration tests, future adapters).
builder.Services.AddSurrealForge(builder.Configuration, surrealRuntimeConfigSection);
// Decorate ISurrealExecutor with OTEL instrumentation (spans + SurrealMetrics).
// The decorator is in AZOA.WebAPI so the homebake package stays observability-agnostic.
// Remove the package's DefaultSurrealExecutor descriptor and re-register the same
// implementation via ActivatorUtilities so the InstrumentedSurrealExecutor wraps it
// without leaving a dangling registration in DI (GetServices<ISurrealExecutor>()
// would otherwise return two entries; the runbook's M1 finding).
{
    var defaultExecutorDescriptor = builder.Services.Single(d =>
        d.ServiceType == typeof(SurrealForge.Client.Query.ISurrealExecutor));
    builder.Services.Remove(defaultExecutorDescriptor);
    builder.Services.AddScoped<SurrealForge.Client.Query.ISurrealExecutor>(sp =>
    {
        var inner = (SurrealForge.Client.Query.ISurrealExecutor)
            ActivatorUtilities.CreateInstance(sp, defaultExecutorDescriptor.ImplementationType!);
        return new AZOA.WebAPI.Observability.InstrumentedSurrealExecutor(inner);
    });
}
// </surrealdb-client-package>

// SwapManager's quote cache is now an injected, bounded IMemoryCache (was a
// process-static dictionary). SizeLimit is required because every cache write
// sets per-entry Size=1; without a limit the SetSize call throws.
builder.Services.AddMemoryCache(o => o.SizeLimit = 1024);

builder.Services.AddScoped<IAvatarManager, AvatarManager>();
// final-hardening-cutover H2: operator:admin JWT-mint bootstrap. Stamping itself
// lives in AvatarManager.GenerateJwt (stateless, per-mint); this hosted service
// only makes a PARTIAL AdminBootstrap config loud at boot. See Services/Admin/AGENTS.md.
builder.Services.AddOptions<AZOA.WebAPI.Services.Admin.AdminBootstrapOptions>()
    .Bind(builder.Configuration.GetSection(AZOA.WebAPI.Services.Admin.AdminBootstrapOptions.SectionName));
builder.Services.AddHostedService<AZOA.WebAPI.Services.Admin.SeedAdminHostedService>();
builder.Services.AddSingleton<WalletKeyService>();
builder.Services.AddSingleton<IAlgorandFaucet, AlgorandFaucet>();
builder.Services.AddScoped<IWalletManager, WalletManager>();
builder.Services.AddScoped<IHolonManager, HolonManager>();
// final-hardening-cutover F5: opt-in Holon AssetType registry manager (CRUD + the
// validation hook HolonManager consults on every holon create/update).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IHolonTypeRegistryManager,
    AZOA.WebAPI.Managers.HolonTypeRegistryManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeGovernanceManager,
    AZOA.WebAPI.Managers.NodeGovernanceManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeFeeScheduleManager,
    AZOA.WebAPI.Managers.NodeFeeScheduleManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeTreasuryManager,
    AZOA.WebAPI.Managers.NodeTreasuryManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeTransparencyManager,
    AZOA.WebAPI.Managers.NodeTransparencyManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeTransparencyHistoryService,
    AZOA.WebAPI.Managers.NodeTransparencyHistoryService>();
builder.Services.AddSingleton<AZOA.WebAPI.Services.Governance.NodeTransparencyCursorCodec>();

// ─── Custodial-provider initiative: custody / tenant / kyc / allocation managers ───
// custody-key-management: decrypt→sign→zero resolver (the only signer-facing
// key path besides WalletManager.ExportWalletAsync). Scoped to match IWalletStore.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IKeyCustodyService,
    AZOA.WebAPI.Services.Custody.KeyCustodyService>();
// final-hardening B5: live wrapping-key rotation orchestration (dual-key window,
// batch re-wrap, idempotent/resumable, all-or-nothing rollback) on top of
// IKeyCustodyService.RewrapAsync. Scoped to match IWalletStore / IKeyCustodyService.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IKeyRotationService,
    AZOA.WebAPI.Services.Custody.KeyRotationService>();
// security-review HIGH-1: durable, out-of-DB pending-rotation marker (verifier token
// only, never the raw key) so a discarded-new-key scenario after a partial rotation is
// recoverable. Singleton — thin stateless wrapper over one on-disk file.
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.Managers.IPendingRotationKeyStore,
    AZOA.WebAPI.Services.Custody.FilePendingRotationKeyStore>();
// tenant-onboarding: tenant principal provisioning + cross-tenant isolation.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.ITenantManager,
    AZOA.WebAPI.Managers.TenantManager>();

// ─── user-self-sovereignty: consent gate + managers + webhook delivery ───────
// tenant-consent-delegation C1/AC4: the LIVE consent check the custody seam calls
// before any tenant-driven key decrypt. KeyCustodyService depends on this — it is
// the single chokepoint's fail-closed authority.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.ITenantConsentGate,
    AZOA.WebAPI.Services.Consent.TenantConsentGate>();
// The consent authority (grant/revoke/list) + the thin outbox emitter it calls.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IConsentManager,
    AZOA.WebAPI.Managers.ConsentManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IConsentWebhookEmitter,
    AZOA.WebAPI.Services.Consent.ConsentWebhookEmitter>();
// user-sovereign-identity: wallet-challenge auth + claim flow.
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.IWalletSignatureVerifier,
    AZOA.WebAPI.Services.Signing.Ed25519SignatureVerifier>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IWalletAuthManager,
    AZOA.WebAPI.Managers.WalletAuthManager>();
// Webhook bridge (AC7/AC8): SSRF guard + timestamped HMAC signer (singletons) +
// the polling delivery worker (hosted). Config-driven via the "Webhooks" section;
// Enabled defaults to false until a tenant endpoint is registered.
builder.Services.AddSingleton<AZOA.WebAPI.Core.Webhooks.WebhookSsrfGuard>();
builder.Services.AddSingleton<AZOA.WebAPI.Core.Webhooks.WebhookHmacSigner>();
builder.Services.AddOptions<AZOA.WebAPI.Services.Webhooks.WebhookOptions>()
    .Bind(builder.Configuration.GetSection(AZOA.WebAPI.Services.Webhooks.WebhookOptions.SectionName));
builder.Services.AddHttpClient(AZOA.WebAPI.Services.Webhooks.WebhookOptions.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = AZOA.WebAPI.Core.Webhooks.WebhookSsrfGuard.CreateGuardedConnectCallback(),
    });
builder.Services.AddHostedService<AZOA.WebAPI.Services.Webhooks.ConsentWebhookDeliveryWorker>();
// final-hardening F3: the GENERIC quest.emit webhook path. The Emit node's enqueue seam
// (QuestWebhookEmitter) + a named HttpClient using the SAME SSRF-guarded, no-auto-redirect
// primary handler as the consent client + the parallel hosted delivery worker. Reuses the
// shared WebhookSsrfGuard / WebhookHmacSigner / WebhookOptions / IWebhookRegistrationStore
// registered above — a tenant's ONE registration receives both consent and quest.emit events.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IQuestWebhookEmitter,
    AZOA.WebAPI.Services.Quest.QuestWebhookEmitter>();
builder.Services.AddHttpClient(AZOA.WebAPI.Services.Webhooks.WebhookOptions.QuestHttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = AZOA.WebAPI.Core.Webhooks.WebhookSsrfGuard.CreateGuardedConnectCallback(),
    });
builder.Services.AddHostedService<AZOA.WebAPI.Services.Webhooks.QuestWebhookDeliveryWorker>();
// kyc-module: KycSettings bound from the "Kyc" section; the provider is selected
// by Kyc:Provider (manual default; veriff = config-gated deploy-stub, throws).
builder.Services.Configure<AZOA.WebAPI.Settings.KycSettings>(
    builder.Configuration.GetSection(AZOA.WebAPI.Settings.KycSettings.SectionName));
if (string.Equals(
        builder.Configuration[$"{AZOA.WebAPI.Settings.KycSettings.SectionName}:Provider"],
        "veriff", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService,
        AZOA.WebAPI.Providers.Kyc.VeriffKycProviderService>();
}
else
{
    builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService,
        AZOA.WebAPI.Providers.Kyc.ManualKycProviderService>();
}
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IKycManager,
    AZOA.WebAPI.Managers.KycManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IKycGateService,
    AZOA.WebAPI.Services.Kyc.KycGateService>();
builder.Services.Configure<AZOA.WebAPI.Services.Governance.NodeGovernanceOptions>(
    builder.Configuration.GetSection(AZOA.WebAPI.Services.Governance.NodeGovernanceOptions.SectionName));
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.INodeGovernanceGuard,
    AZOA.WebAPI.Services.Governance.NodeGovernanceGuard>();
// fiat-stripe-bridge: idempotent, KYC-gated, tenant-callable allocation primitive.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IAllocationManager,
    AZOA.WebAPI.Managers.AllocationManager>();
// fungible-token-node: idempotent, KYC-gated fungible-token (ASA) launch seam.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IFungibleTokenManager,
    AZOA.WebAPI.Managers.FungibleTokenManager>();
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
    client.DefaultRequestHeaders.Add("User-Agent", "AZOA-SwapManager/1.0");
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
builder.Services.AddScoped<AZOA.WebAPI.Services.Quest.QuestConfigBindingResolver>();
builder.Services.AddScoped<IQuestManager, QuestManager>();

// <dapp-composition>
// IDappSeriesStore + IDappCompositionManager are the dapp-composition track's
// surfaces. The store operates on source-gen'd DappSeries + DappSeriesQuest
// POCOs (AZOA.WebAPI.Persistence.SurrealDb.Models) -- no hand-written entity types
// for this aggregate. InMemory is the default until surrealdb-migration
// wave-2 lands the Surreal-backed adapter; the Singleton lifetime matches
// the existing InMemory store pattern (process-lifetime state).
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.Stores.IDappSeriesStore,
    AZOA.WebAPI.Providers.Stores.InMemoryDappSeriesStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IDappCompositionManager,
    AZOA.WebAPI.Managers.DappCompositionManager>();
// </dapp-composition>

// ─── Transaction signing seam (signing-core-keystone) ───
// Real server-side signing behind a chain-agnostic ITransactionSigner, selected
// by ChainType via TransactionSignerFactory (mirrors BlockchainProviderFactory).
// Adding a chain is one new ITransactionSigner registration here.
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.Signing.ITransactionSigner,
    AZOA.WebAPI.Providers.Blockchain.Algorand.AlgorandTransactionSigner>();
// Solana signer: real Ed25519 signing via Solnet.Wallet (final-hardening B1).
// See Providers/Blockchain/Solana/AGENTS.md §signer for the canonical byte contract.
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.Signing.ITransactionSigner,
    AZOA.WebAPI.Providers.Blockchain.Solana.SolanaTransactionSigner>();
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.Signing.ITransactionSignerFactory>(sp =>
    new AZOA.WebAPI.Core.Signing.TransactionSignerFactory(
        sp.GetServices<AZOA.WebAPI.Interfaces.Signing.ITransactionSigner>()));

// ─── Blockchain providers & factory ───
// Providers are transient because the factory binds one mutable instance to one
// chain/network pair and caches that independent binding for its lifetime.
builder.Services.AddTransient<AlgorandProvider>(sp => new AlgorandProvider(
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<AlgorandProvider>>(),
    sp.GetRequiredService<AZOA.WebAPI.Interfaces.Signing.ITransactionSignerFactory>(),
    sp.GetRequiredService<WalletKeyService>(),
    // value-path-wiring C1: route signing through the audited custody choke point.
    // IKeyCustodyService is scoped, so the network-bound provider resolves it
    // per signing operation from a fresh scope. Passing null for the direct
    // custody seam keeps that injection for unit tests only.
    custodyService: null,
    custodyScopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
    // Faucet operations are provider-scoped: the Algorand provider owns the
    // test-ALGO top-up path and delegates the idempotent build/sign/submit to the
    // injected IAlgorandFaucet (WalletManager no longer depends on it directly).
    faucet: sp.GetRequiredService<IAlgorandFaucet>()));
builder.Services.AddTransient<SolanaProvider>();
builder.Services.AddTransient<AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider>();
builder.Services.AddSingleton<IBlockchainProviderFactory>(sp =>
    new BlockchainProviderFactory(
        new[]
        {
            new BlockchainProviderRegistration("Algorand", sp.GetRequiredService<AlgorandProvider>),
            new BlockchainProviderRegistration("Solana", sp.GetRequiredService<SolanaProvider>),
            new BlockchainProviderRegistration(
                "Simulated",
                sp.GetRequiredService<AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider>),
        },
        sp.GetRequiredService<IConfiguration>()));

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
// via IServiceScopeFactory. surrealdb-migration wave-2 task 7 flipped the
// impl from EF (AZOA.WebAPI.Core.Idempotency.IdempotencyStore) to the
// SurrealDB-backed SurrealIdempotencyStore, which closes the C5
// multi-statement-swallow risk via per-statement SurrealResponse inspection
// and writes through deterministic SHA-256(key) record ids.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IIdempotencyStore,
    AZOA.WebAPI.Core.Idempotency.SurrealIdempotencyStore>();

// ─── Cross-chain bridge (hybrid trusted + Wormhole) ───
// surrealdb-migration wave-2 task 8: routes through IBridgeStore +
// IIdempotencyStore. Storage backend is SurrealDB after wave-3 EF deletion.
// bridge-safety-hardening Phase A: kill-switch options (RealValueEnabled defaults false).
builder.Services.AddOptions<AZOA.WebAPI.Services.Bridge.BridgeOptions>()
    .Bind(builder.Configuration.GetSection(
        AZOA.WebAPI.Services.Bridge.BridgeOptions.SectionName));
builder.Services.AddScoped<ICrossChainBridgeService, CrossChainBridgeService>();

// ─── Chain reconciliation (api-safety-hardening tasks 14/15) ───
// Scoped service re-derives true status from chain truth; the hosted service
// drives a periodic sweep, creating a DI scope per tick. Routes all bridge +
// operation reads/writes through IBridgeStore (surrealdb-migration wave-2
// task 8 — completed).
builder.Services.AddOptions<AZOA.WebAPI.Services.Reconciliation.ReconciliationOptions>()
    .Bind(builder.Configuration.GetSection(
        AZOA.WebAPI.Services.Reconciliation.ReconciliationOptions.SectionName));
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IReconciliationService,
    AZOA.WebAPI.Services.Reconciliation.ReconciliationService>();
builder.Services.AddHostedService<AZOA.WebAPI.Services.Reconciliation.ReconciliationHostedService>();

// ─── Durable saga / transactional outbox (durable-saga-orchestration Phase 1) ───
// Generic, reusable, bridge-agnostic. Mirrors the reconciliation registrations:
// options-bound section, scoped store/processor (per-tick DI scope), a hosted
// processor driven by a swappable polling trigger (SurrealDB LIVE-query later).
builder.Services.AddOptions<AZOA.WebAPI.Sagas.SagaOptions>()
    .Bind(builder.Configuration.GetSection(
        AZOA.WebAPI.Sagas.SagaOptions.SectionName));
// surrealdb-migration wave-2 task 8b: flip ISagaStore to the SurrealDB-backed
// impl. The G2 single-winner claim now executes via a parameterized
// UPDATE … WHERE id == $id AND status == 'Pending' AND next_run_at <= $now
// against the new saga_steps table (Persistence/SurrealDb/Schemas/080_saga_steps.surql).
builder.Services.AddScoped<AZOA.WebAPI.Sagas.ISagaStore,
    AZOA.WebAPI.Sagas.SurrealSagaStore>();
builder.Services.AddSingleton<AZOA.WebAPI.Sagas.ISagaRegistry,
    AZOA.WebAPI.Sagas.SagaRegistry>();
builder.Services.AddScoped<AZOA.WebAPI.Sagas.ISagaCoordinator,
    AZOA.WebAPI.Sagas.SagaCoordinator>();
builder.Services.AddScoped<AZOA.WebAPI.Sagas.ISagaProcessor,
    AZOA.WebAPI.Sagas.SagaProcessor>();
builder.Services.AddSingleton<AZOA.WebAPI.Sagas.ISagaTrigger,
    AZOA.WebAPI.Sagas.PollingSagaTrigger>();
builder.Services.AddHostedService<AZOA.WebAPI.Sagas.SagaProcessorHostedService>();

// ─── Durable workflow engine (durable-workflow-engine) ───
// The FIRST real saga consumer: a single "quest-workflow" ISagaDefinition whose
// type-uniform FindStep resolves every node-id step to one self-advancing node
// handler (Approach A). The definition is pure metadata (Singleton); the step
// handlers wrap scoped quest stores (Scoped). The two handlers close over
// DISTINCT payload types so GetRequiredService<IStepHandler<T>> is unambiguous.
builder.Services.AddSingleton<AZOA.WebAPI.Sagas.ISagaDefinition,
    AZOA.WebAPI.Services.Quest.Workflow.QuestWorkflowSagaDefinition>();
builder.Services.AddScoped<
    AZOA.WebAPI.Sagas.IStepHandler<AZOA.WebAPI.Services.Quest.Workflow.QuestStepPayload>,
    AZOA.WebAPI.Services.Quest.Workflow.QuestNodeStepHandler>();
builder.Services.AddScoped<
    AZOA.WebAPI.Sagas.IStepHandler<AZOA.WebAPI.Services.Quest.Workflow.QuestCompensatePayload>,
    AZOA.WebAPI.Services.Quest.Workflow.QuestCompensateStepHandler>();

// ─── MCP surface (mcp-surface track Phase 1) ───
// Registers McpToolRegistry (singleton) + the SDK's Streamable HTTP transport
// at /mcp. Tool implementations (W2-W5) register themselves as IMcpTool via DI;
// McpToolRegistry's ctor enumerates them at first resolution.
builder.Services.AddMcpSurface();

// ─── Quest DAG system ───
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IQuestDagValidator, AZOA.WebAPI.Services.QuestDagValidator>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IQuestDagExecutabilityValidator, AZOA.WebAPI.Services.Quest.QuestDagExecutabilityValidator>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IQuestInstantiator, AZOA.WebAPI.Services.Quest.QuestInstantiator>();

// Quest node handlers — exactly one per QuestNodeType. Registered Scoped: each
// handler wraps scoped managers, so Singleton would capture a disposed scope
// (captive-dependency bug). Discovered by assembly scan over the single sealed
// Services/Quest/Handlers namespace so adding a handler needs no DI edit; the
// registry's ctor still fails fast on a duplicate/missing QuestNodeType.
foreach (var handlerType in typeof(QuestNodeHandlerRegistry).Assembly
             .GetTypes()
             .Where(t => t is { IsClass: true, IsAbstract: false }
                         && t.Namespace == "AZOA.WebAPI.Services.Quest.Handlers"
                         && typeof(IQuestNodeHandler).IsAssignableFrom(t)))
{
    builder.Services.AddScoped(typeof(IQuestNodeHandler), handlerType);
}
builder.Services.AddScoped<IQuestNodeHandlerRegistry, QuestNodeHandlerRegistry>();

// ─── Observability (W5): OpenTelemetry tracing/metrics + /health ───
builder.Services.AddAzoaObservability(builder.Configuration);
builder.Services.AddAzoaHealthChecks();

// Validate Cors:AllowedOrigins in Production at startup
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("IntegrationTest"))
{
    var origins = builder.Configuration
        .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
    if (origins.Length == 0)
        throw new InvalidOperationException(
            "Cors:AllowedOrigins is empty outside Development. Set the allowed " +
            "origin list via the Cors__AllowedOrigins__0 (etc.) environment " +
            "variables before starting in Production.");
}

builder.Services.AddSingleton<Microsoft.AspNetCore.Cors.Infrastructure.ICorsPolicyProvider, AZOA.WebAPI.Middleware.DynamicCorsPolicyProvider>();
builder.Services.AddCors();

var app = builder.Build();

// Verbose error reporting.
//   • Opt-in via AZOA:DebugErrors (env: AZOA__DebugErrors); defaults on in Development.
//   • HARD GUARDRAIL: Production can NEVER emit verbose debug, no matter what
//     config or environment variables say. Only platform devs running a
//     non-Production environment can turn this on — so stack traces and other
//     internals can never leak from a production deployment.
var debugRequested = builder.Configuration.GetValue<bool?>("AZOA:DebugErrors")
    ?? app.Environment.IsDevelopment();
AZOAResultDebug.Enabled = debugRequested && !app.Environment.IsProduction();

app.Logger.LogInformation(
    "Verbose error debug is {State} (environment={Environment}, requested={Requested}).",
    AZOAResultDebug.Enabled ? "ENABLED" : "disabled",
    app.Environment.EnvironmentName,
    debugRequested);
if (debugRequested && app.Environment.IsProduction())
    app.Logger.LogWarning(
        "AZOA:DebugErrors was requested but is FORCE-DISABLED in Production.");

// Resolve trusted forwarding before rate limiting/auth. Direct/self-hosted
// deployments default to disabled; trust-all requires an explicit edge-only ack.
var forwardedHeadersOptions = ForwardedHeaderTrust.Build(builder.Configuration);
if (forwardedHeadersOptions is not null)
    app.UseForwardedHeaders(forwardedHeadersOptions);

// Must be the first error-handling middleware so it wraps the entire pipeline
// and turns any unhandled throw into a structured (debug-aware) JSON error
// instead of a blank HTTP 500.
app.UseMiddleware<DebugExceptionMiddleware>();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("IntegrationTest"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// SurrealDB boot self-check (G1 reachability + durability acknowledgement).
//
// G1 durability on SurrealDB 3.x is enforced by the RocksDB storage engine
// (`rocksdb:///data/db` passed to `surreal start`) plus the
// `SURREAL_SYNC_DATA: "true"` env var, which makes RocksDB fsync its WAL on
// every commit before ACK. Both live in docker-compose.dev.yml (and the
// Railway deploy manifest). The retired 1.5.x `surrealkv://...?sync=every`
// URI parameter is NOT used — the v3.1.4 cutover kept rocksdb (see
// conductor/tracks/_archive/surrealdb-major-upgrade/DECISION.md).
//
// SurrealDB exposes no SQL surface to read back the fsync mode at runtime
// (memory [[surrealdb-fsync-mode-not-introspectable]]), so this code path
// cannot behaviourally verify durability; it stays a deploy-time review item
// (the static G1_DurabilityAckGate test asserts the compose posture). What we
// CAN verify at boot is:
//   (1) the server is reachable through the same ISurrealExecutor the rest
//       of the app uses (proves DI + connection + auth all line up), and
//   (2) the SurrealRuntime:G1DurabilityAcknowledged config flag is set to true
//       — operators must explicitly ack that they've reviewed compose and
//       confirmed the durable posture. This is an audit-trail gate,
//       not a behavioural one.
// IntegrationTest environments skip both checks because the test container
// is brought up per-test by the harness, not at host boot.
if (!app.Environment.IsEnvironment("IntegrationTest"))
{
    var ack = builder.Configuration.GetValue<bool>("SurrealRuntime:G1DurabilityAcknowledged");
    if (!ack)
        throw new InvalidOperationException(
            "SurrealDB G1 durability acknowledgement is missing. Confirm that " +
            "docker-compose.dev.yml (or your deploy manifest) passes " +
            "`rocksdb:///data/db` to `surreal start` AND sets " +
            "`SURREAL_SYNC_DATA: \"true\"` (RocksDB then fsyncs its WAL on every " +
            "commit before ACK — the 3.x durable path that replaced the retired " +
            "`surrealkv://...?sync=every`), then set " +
            "SurrealRuntime:G1DurabilityAcknowledged=true in configuration to " +
            "acknowledge the review. Every commit must fsync before ack (G1).");

    using var scope = app.Services.CreateScope();
    var executor = scope.ServiceProvider.GetRequiredService<
        SurrealForge.Client.Query.ISurrealExecutor>();
    try
    {
        // RETURN 1; is the idiomatic SurrealQL no-op probe -- SELECT
        // requires FROM in 1.5+ and was being rejected with a parse
        // error here, masking the real "server reachable" intent.
        await executor.ExecuteAsync(
            SurrealForge.Client.Query.SurrealQuery.Of("RETURN 1"));
    }
    catch (Exception ex)
    {
        var endpoint = builder.Configuration[$"{surrealRuntimeConfigSection}:Endpoint"]
            ?? "http://localhost:8000";
        throw new InvalidOperationException(
            "SurrealDB server unreachable at boot. Ensure the container is running " +
            $"and reachable at {endpoint}. Review docker-compose.dev.yml and " +
            "confirm health checks are passing.",
            ex);
    }
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("Default");
// W1-A1: Observer middleware — captures 401/429/5xx and unhandled exceptions into JSONL logs.
// Placed after UseRouting (so TraceIdentifier is stable) and before UseAuthentication so
// that downstream 401 and 429 status codes are also captured.
app.UseMiddleware<AZOA.WebAPI.Core.Diagnostics.JsonlExceptionMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
// Rate limiter AFTER auth so the partition key can fall back to the
// authenticated avatar/user when no X-Api-Key header is present.
app.UseRateLimiter();
// W5 request correlation: after UseRouting, before MapControllers — attaches
// the W3C TraceId/SpanId as a structured log scope for every request.
app.UseAzoaRequestCorrelation();
// MCP surface (mcp-surface track): /mcp endpoint protected by the existing
// JWT+ApiKey multi-scheme via RequireAuthorization() inside MapMcp().
// UseMcpAuth (Phase 2 W4) extracts the AvatarId claim into ctx.Items so the
// MCP dispatcher can lift it into ToolCallContext for per-tool avatar scoping.
// Placed after UseAuthentication/UseAuthorization so the auth pipeline runs
// before MCP request dispatch.
app.UseMcpAuth();
app.MapMcp();
// ISwapManager + IDexAdapter registrations are above (DEX adapters block)
app.MapControllers();
app.MapNodeConformanceDocument();
app.MapAzoaHealth(app.Environment);
await app.RunAsync();

public partial class Program { }
