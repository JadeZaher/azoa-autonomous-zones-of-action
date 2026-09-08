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
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Middleware;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Auth;
using AZOA.WebAPI.Services.Users;
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
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Mcp;
using AZOA.WebAPI.Services.Actions.Handlers;
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
AZOA.WebAPI.Services.Kyc.KycRuntimeSafety.GuardStartup(builder.Environment, builder.Configuration);
// Production-readiness FR-5 AC3: a Production host must resolve a REAL identity
// provider. Keep this list in sync with the adapter keys registered in
// KycProviderRegistry's _adapters dictionary below. Reads Kyc:Provider directly for
// the same reason the blockchain guard reads Blockchain:Chains directly — the
// runtime selection path silently substitutes the unavailable provider for an
// unknown key, which would hide the exact defect this catches.
AZOA.WebAPI.Services.Kyc.KycRuntimeSafety.GuardProductionProviderIsReal(
    builder.Environment,
    builder.Configuration,
    AZOA.WebAPI.Services.Kyc.KycRegisteredAdapters.RealKeys);

// Federated sign-in fail-fast guard (Phase 1B D18). Same shape as the two guards
// above: a provider ADVERTISED as enabled whose client secret is absent stops the
// host, rather than booting healthy and presenting a sign-in button that 500s on
// the first user who presses it. Returns the bound options so the scheme
// registration below and the DI registration share one validated instance.
var externalAuthOptions = AZOA.WebAPI.Services.Auth.ExternalAuthRuntimeSafety.GuardStartup(
    builder.Environment, builder.Configuration);
builder.Services.Configure<AZOA.WebAPI.Services.Auth.ExternalAuthOptions>(
    builder.Configuration.GetSection(AZOA.WebAPI.Services.Auth.ExternalAuthOptions.SectionName));

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
        IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)),
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    // user-sovereign-identity AC3b (security-review fix): the per-user
    // AuthNotBefore watermark MUST be re-checked on EVERY request, not only when a
    // child credential is minted. ValidateLifetime alone checks the token's OWN nbf
    // against now — it has no knowledge of the user's watermark. Without this, a
    // tenant child JWT minted seconds before the user claims their user stays valid
    // to its natural exp (up to 15 min) AFTER the user severed custody. This event
    // loads the subject user and FAILS the token when it was issued before the
    // current watermark. Fail-closed: a lookup error rejects the token.
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var principal = context.Principal;
            if (AZOA.WebAPI.Core.AzoaClaims.IsChildCredential(principal))
            {
                context.Fail("Delegated child credentials are disabled until accepting endpoints are explicitly scope-allowlisted.");
                return;
            }

            if (AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(principal))
            {
                var operatorPolicies = context.HttpContext.GetEndpoint()?.Metadata
                    .GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                    .Select(metadata => metadata.Policy)
                    .Where(policy => !string.IsNullOrWhiteSpace(policy))
                    .ToHashSet(StringComparer.Ordinal)
                    ?? new HashSet<string>(StringComparer.Ordinal);
                if (!operatorPolicies.Overlaps([
                        "NodeOperatorSession",
                        "RecentNodeOperatorSession",
                        "DappRoleAssignment",
                        "Operator",
                        "NodeGovern"]))
                {
                    context.Fail("Node operator sessions are not accepted by this endpoint.");
                    return;
                }

                var operatorSubject = principal?.FindFirst("sub")?.Value
                    ?? principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(operatorSubject, out var operatorUserId)
                    || operatorUserId != AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId
                    || principal!.GetActingTenantId() is not null
                    || !long.TryParse(
                        principal.FindFirst(AZOA.WebAPI.Core.AzoaClaims.OperatorRevision)?.Value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var tokenRevision)
                    || !long.TryParse(
                        principal.FindFirst(AZOA.WebAPI.Core.AzoaClaims.OperatorSessionRevision)?.Value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var tokenSessionRevision))
                {
                    context.Fail("Node operator credential is malformed.");
                    return;
                }

                var stateStore = context.HttpContext.RequestServices
                    .GetRequiredService<AZOA.WebAPI.Interfaces.Stores.IAdminBootstrapStateStore>();
                var state = await stateStore.GetAsync(context.HttpContext.RequestAborted);
                var expectedLink = SurrealForge.Client.SurrealLink.ToLink(
                    "user",
                    SurrealForge.Client.SurrealId.ToSurrealId(operatorUserId));
                if (state.IsError
                    || state.Result is null
                    || state.Result.CredentialRevision != tokenRevision
                    || state.Result.SessionRevision != tokenSessionRevision
                    || !string.Equals(state.Result.UserId, expectedLink, StringComparison.Ordinal))
                {
                    context.Fail("Node operator credential revision is stale.");
                    return;
                }
            }

            var sub = principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                      ?? principal?.FindFirst("sub")?.Value;
            if (!Guid.TryParse(sub, out var userId))
            {
                // No resolvable subject — nothing user-scoped to enforce; the rest
                // of validation already passed. (Non-user principals are rare here.)
                return;
            }

            var userStore = context.HttpContext.RequestServices
                .GetRequiredService<AZOA.WebAPI.Interfaces.Stores.IUserStore>();
            var loaded = await userStore.GetByIdAsync(userId, context.HttpContext.RequestAborted);
            if (loaded.IsError || loaded.Result is null)
            {
                // Fail-closed: a token whose subject user cannot be loaded is rejected.
                context.Fail("Subject user could not be verified.");
                return;
            }

            if (userId == AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId
                && !AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(principal))
            {
                context.Fail("The reserved node operator subject requires a node-operator session.");
                return;
            }

            if (AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(principal)
                && (!loaded.Result.IsActive
                    || loaded.Result.Id != AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId
                    || !string.Equals(
                        loaded.Result.Email,
                        AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.ReservedEmail,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        loaded.Result.Username,
                        principal?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value,
                        StringComparison.Ordinal)
                    || !AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.IsValidUsername(loaded.Result.Username)
                    || !AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.IsStructurallyValidPasswordHash(loaded.Result.PasswordHash)))
            {
                context.Fail("Node operator identity integrity check failed.");
                return;
            }

            var watermark = loaded.Result.AuthNotBefore;
            if (watermark is null)
                return; // never claimed / no watermark — nothing to enforce.

            // The token's issued-at / not-before must be at/after the watermark. A
            // token minted before the user's last claim is stale and rejected,
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
                context.Fail("Token was issued before the user's authentication watermark (post-claim cut).");
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

// ── Federated sign-in schemes (Phase 1B, D18) ────────────────────────────────
//
// A short-lived cookie carries the provider result the few milliseconds between the
// handler's own callback path and ExternalAuthController.Callback, which immediately
// consumes and deletes it. This is ASP.NET Identity's external-login shape. It is NOT
// an application session: no endpoint authenticates against it, the MultiScheme
// selector never forwards to it, and the controller signs it out on every path
// (success and failure alike).
//
// SameSite=Lax is required, not a preference: the provider returns via a top-level
// cross-site GET, and Strict would drop the cookie and break every sign-in.
if (externalAuthOptions.EnabledProviderNames().Count > 0)
{
    builder.Services.AddAuthentication()
        .AddCookie(AZOA.WebAPI.Controllers.ExternalAuthController.ExternalScheme, options =>
        {
            options.Cookie.Name = "azoa.oauth.external";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            options.SlidingExpiration = false;
        });
}

if (externalAuthOptions.Google.IsUsable)
{
    builder.Services.AddAuthentication().AddGoogle(
        AZOA.WebAPI.Models.Auth.ExternalAuthProviderNames.Google,
        options =>
        {
            options.ClientId = externalAuthOptions.Google.ClientId;
            options.ClientSecret = externalAuthOptions.Google.ClientSecret;
            options.SignInScheme = AZOA.WebAPI.Controllers.ExternalAuthController.ExternalScheme;
            options.CallbackPath = "/auth/oauth/google/signin";

            // state + PKCE. UsePkce defaults true on this handler; setting it
            // explicitly makes the guarantee visible at the registration site and
            // fails loudly if a future upgrade changes the default. The handler
            // generates the verifier, keeps it inside the DataProtection-encrypted
            // state, and refuses a callback whose correlation cookie does not match.
            options.UsePkce = true;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");

            options.SaveTokens = false;
        });
}

if (externalAuthOptions.GitHub.IsUsable)
{
    // Generic OAuth rather than a third-party package: the handler already implements
    // the authorization-code exchange, so the only GitHub-specific parts are the three
    // endpoint URLs and the claim mapping.
    builder.Services.AddAuthentication().AddOAuth(
        AZOA.WebAPI.Models.Auth.ExternalAuthProviderNames.GitHub,
        options =>
        {
            options.ClientId = externalAuthOptions.GitHub.ClientId;
            options.ClientSecret = externalAuthOptions.GitHub.ClientSecret;
            options.SignInScheme = AZOA.WebAPI.Controllers.ExternalAuthController.ExternalScheme;
            options.CallbackPath = "/auth/oauth/github/signin";

            options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            options.TokenEndpoint = "https://github.com/login/oauth/access_token";
            options.UserInformationEndpoint = "https://api.github.com/user";

            // GitHub OAuth Apps ignore code_challenge today, so `state` is the control
            // that actually binds this callback to this browser there. PKCE is still
            // requested: it costs nothing, it is honoured by GitHub Apps, and leaving
            // it off would mean a future GitHub rollout silently failed to protect us.
            options.UsePkce = true;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.Scope.Clear();
            options.Scope.Add("read:user");
            options.Scope.Add("user:email");

            options.SaveTokens = false;

            // GitHub's numeric `id` is the STABLE subject. `login` is a display handle
            // the user can change and MUST NOT key the binding; `email` must not either
            // (D17). Mapping id -> NameIdentifier is what ExternalIdentityReader reads.
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.NameIdentifier, "id");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Name, "login");
            options.ClaimActions.MapJsonKey(System.Security.Claims.ClaimTypes.Email, "email");

            options.Events = new Microsoft.AspNetCore.Authentication.OAuth.OAuthEvents
            {
                OnCreatingTicket = async context =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                    request.Headers.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.AccessToken);
                    // GitHub rejects requests without a User-Agent.
                    request.Headers.UserAgent.ParseAdd("AZOA-WebAPI");

                    using var response = await context.Backchannel.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead, context.HttpContext.RequestAborted);
                    response.EnsureSuccessStatusCode();

                    using var payload = System.Text.Json.JsonDocument.Parse(
                        await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                    context.RunClaimActions(payload.RootElement);
                }
            };
        });
}

// TenantScope policy (tenant-onboarding): a tenant-surface action requires the
// tenant:provision scope claim (emitted per-CSV-entry by ApiKeyAuthenticationHandler).
//
// Operator policy (security-review HIGH-2): gates the MOST destructive surfaces in the
// tree — live wrapping-key rotation and data backfill, both of which rewrite state
// across every user. Hardened along two independent axes so no single misconfig
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
    o.AddPolicy("FirstPartyLogin", p =>
    {
        p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
        {
            var isFirstPartyLogin = string.Equals(
                ctx.User.FindFirst(AZOA.WebAPI.Core.AzoaClaims.TokenUse)?.Value,
                AZOA.WebAPI.Core.AzoaClaims.TokenUseLogin,
                StringComparison.Ordinal)
                && ctx.User.GetActingTenantId() is null
                && !string.Equals(
                ctx.User.FindFirst("AuthMethod")?.Value,
                "ApiKey",
                StringComparison.OrdinalIgnoreCase);
            if (isFirstPartyLogin)
                return true;

            if (!AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(ctx.User)
                || ctx.Resource is not HttpContext httpContext)
            {
                return false;
            }

            var endpointPolicies = httpContext.GetEndpoint()?.Metadata
                .GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Select(metadata => metadata.Policy)
                .Where(policy => !string.IsNullOrWhiteSpace(policy))
                .ToHashSet(StringComparer.Ordinal);
            return endpointPolicies?.Overlaps([
                "Operator",
                "RecentNodeOperatorSession",
                "NodeOperatorSession"]) == true;
        });
    });
    o.AddPolicy("RecentFirstPartyLogin", p =>
    {
        p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
        {
            if (!string.Equals(
                    ctx.User.FindFirst(AZOA.WebAPI.Core.AzoaClaims.TokenUse)?.Value,
                    AZOA.WebAPI.Core.AzoaClaims.TokenUseLogin,
                    StringComparison.Ordinal)
                || ctx.User.GetActingTenantId() is not null
                || string.Equals(
                    ctx.User.FindFirst("AuthMethod")?.Value,
                    "ApiKey",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!long.TryParse(
                    ctx.User.FindFirst(AZOA.WebAPI.Core.AzoaClaims.AuthTime)?.Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var authTime))
            {
                return false;
            }

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(authTime);
            return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(10);
        });
    });
    o.AddPolicy("NodeOperatorSession", p =>
    {
        p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
            AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(ctx.User)
            && AZOA.WebAPI.Core.AzoaClaims.TryGetSubjectId(ctx.User, out var subject)
            && subject == AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId
            && ctx.User.GetActingTenantId() is null
            && !string.Equals(ctx.User.FindFirst("AuthMethod")?.Value, "ApiKey", StringComparison.OrdinalIgnoreCase));
    });
    o.AddPolicy("RecentNodeOperatorSession", p =>
    {
        p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
        {
            if (!AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(ctx.User)
                || !AZOA.WebAPI.Core.AzoaClaims.TryGetSubjectId(ctx.User, out var subject)
                || subject != AZOA.WebAPI.Services.Admin.NodeOperatorIdentity.UserId
                || ctx.User.GetActingTenantId() is not null
                || !long.TryParse(
                    ctx.User.FindFirst(AZOA.WebAPI.Core.AzoaClaims.AuthTime)?.Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var authTime))
            {
                return false;
            }

            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(authTime);
            return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(10);
        });
    });
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
    o.AddPolicy("DappRoleAssignment", p =>
    {
        p.RequireAuthenticatedUser();
        p.RequireAssertion(ctx =>
        {
            if (!AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(ctx.User))
                return ctx.User.HasDappManagerRole();
            if (!ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.Operator)
                || !long.TryParse(
                    ctx.User.FindFirst(AZOA.WebAPI.Core.AzoaClaims.AuthTime)?.Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var authTime))
            {
                return false;
            }
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(authTime);
            return age >= TimeSpan.FromMinutes(-1) && age <= TimeSpan.FromMinutes(10);
        });
    });
    o.AddPolicy("Operator", p =>
        p.RequireAssertion(ctx =>
        {
            // Axis 1: reject API-key-authenticated principals outright. An API key can
            // never be an operator — operator authority originates only from a JWT.
            var authMethod = ctx.User.FindFirst("AuthMethod")?.Value;
            if (string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase))
                return false;

            // Axis 2: require an explicit admin/operator capability on the JWT.
            return AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(ctx.User)
                && ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.Operator)
                && ctx.User.GetActingTenantId() is null;
        }));
    o.AddPolicy("NodeGovern", p =>
        p.RequireAssertion(ctx =>
        {
            var authMethod = ctx.User.FindFirst("AuthMethod")?.Value;
            if (string.Equals(authMethod, "ApiKey", StringComparison.OrdinalIgnoreCase))
                return false;

            return AZOA.WebAPI.Core.AzoaClaims.IsNodeOperator(ctx.User)
                && ctx.User.HasScope(AZOA.WebAPI.Core.AzoaScopes.NodeGovern)
                && ctx.User.GetActingTenantId() is null;
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
//   1. tenant API key            -> partition per authenticated tenant subject
//      (stable across tenant-key rotation); other API keys remain per key id
//   2. else authenticated user -> partition per user/user id
//   3. else anonymous            -> partition per client IP
//
// Three limiters:
//   1. a permissive GLOBAL limiter applied to every endpoint;
//   2. a STRICT named policy ("financial") attached to the irreversible /
//     value-moving endpoints (bridge initiate/redeem/reverse, swap execute,
//     wallet transfer, wallet topup) via [EnableRateLimiting("financial")]; and
//   3. a tenant-custodial subject policy composed with the global tenant
//     aggregate partition.
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
var rlTenantCustodialPermit = rlSection.GetValue<int?>("TenantCustodial:PermitLimit") ?? 20;
var rlTenantCustodialWindowSeconds = rlSection.GetValue<int?>("TenantCustodial:WindowSeconds") ?? 60;
var rlTenantCustodialQueue = rlSection.GetValue<int?>("TenantCustodial:QueueLimit") ?? 0;
// Callback budget: a legitimate provider retries a handful of times per session,
// so a generous-but-bounded per-IP allowance is enough while still cutting off a
// body-secret guessing flood.
var rlKycCallbackPermit = Math.Clamp(
    rlSection.GetValue<int?>("KycCallback:PermitLimit") ?? 60,
    1,
    10_000);
var rlKycCallbackWindowSeconds = Math.Clamp(
    rlSection.GetValue<int?>("KycCallback:WindowSeconds") ?? 60,
    1,
    86_400);
var rlOperatorLoginPermit = Math.Clamp(
    rlSection.GetValue<int?>("OperatorLogin:PermitLimit") ?? 20,
    1,
    10_000);
var rlOperatorLoginWindowSeconds = Math.Clamp(
    rlSection.GetValue<int?>("OperatorLogin:WindowSeconds") ?? 900,
    60,
    86_400);
// Payment-webhook budget (W4.1). The provider retries one event a handful of times with
// backoff, and a busy node sees several distinct payments a minute, so the allowance is
// generous; it exists to bound an unauthenticated flood, not to throttle the provider.
// Set it too low and a legitimate retry burst is rejected with a 429 the provider counts
// as a failed delivery, which is a person charged with nothing credited.
var rlFiatWebhookPermit = Math.Clamp(
    rlSection.GetValue<int?>("FiatWebhook:PermitLimit") ?? 240,
    1,
    10_000);
var rlFiatWebhookWindowSeconds = Math.Clamp(
    rlSection.GetValue<int?>("FiatWebhook:WindowSeconds") ?? 60,
    1,
    86_400);

var rlDevMultiplier = builder.Environment.IsDevelopment()
    ? Math.Max(1, rlSection.GetValue<int?>("DevMultiplier") ?? 1)
    : 1;
rlGlobalPermit    *= rlDevMultiplier;
rlFinancialPermit *= rlDevMultiplier;
rlTenantCustodialPermit *= rlDevMultiplier;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Global limiter — partitioned by identity (api key / user / ip).
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

    // Subject policy composes with the global API-key limiter: one external
    // subject cannot consume every tenant request, while the global partition
    // remains the aggregate ceiling shared by all subjects and rotated keys for that tenant.
    options.AddPolicy("tenant-custodial", httpContext =>
    {
        if (!rlEnabled)
            return RateLimitPartition.GetNoLimiter("disabled");

        var key = RateLimitPartitionKey.ResolveTenantSubject(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rlTenantCustodialPermit,
            Window = TimeSpan.FromSeconds(rlTenantCustodialWindowSeconds),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = rlTenantCustodialQueue
        });
    });

    // Inbound provider callbacks have NO authenticated principal — the shared secret
    // rides in the body — so the identity-partitioned global limiter degenerates to
    // one bucket per source IP anyway. This policy gives the callback its own,
    // stricter budget so a secret-guessing flood cannot also consume the request
    // allowance of every anonymous caller behind the same address.
    options.AddPolicy(AZOA.WebAPI.Controllers.KycCallbackController.RateLimitPolicy, httpContext =>
    {
        var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"kyc-callback:{address}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rlKycCallbackPermit,
            Window = TimeSpan.FromSeconds(rlKycCallbackWindowSeconds),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    options.AddPolicy("operator-login", httpContext =>
    {
        var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"operator-login:{address}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rlOperatorLoginPermit,
            Window = TimeSpan.FromSeconds(rlOperatorLoginWindowSeconds),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    // The payment webhook has NO authenticated principal — its authority is the signature on
    // the body — so the identity-partitioned global limiter degenerates to one bucket per
    // source IP. Its own policy keeps a signature-guessing flood off the anonymous global
    // allowance every other unauthenticated caller shares.
    options.AddPolicy(AZOA.WebAPI.Controllers.StripeWebhookController.RateLimitPolicy, httpContext =>
    {
        var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"fiat-webhook:{address}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rlFiatWebhookPermit,
            Window = TimeSpan.FromSeconds(rlFiatWebhookWindowSeconds),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
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
            var matchedPolicy = context.HttpContext.GetEndpoint()?
                .Metadata.GetMetadata<EnableRateLimitingAttribute>()?
                .PolicyName;
            retryAfterSeconds = matchedPolicy switch
            {
                "financial" => rlFinancialWindowSeconds,
                "tenant-custodial" => rlTenantCustodialWindowSeconds,
                "operator-login" => rlOperatorLoginWindowSeconds,
                AZOA.WebAPI.Controllers.KycCallbackController.RateLimitPolicy =>
                    rlKycCallbackWindowSeconds,
                AZOA.WebAPI.Controllers.StripeWebhookController.RateLimitPolicy =>
                    rlFiatWebhookWindowSeconds,
                _ => rlGlobalWindowSeconds
            };
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
// surrealdb-migration wave-2 close-out (Stream A): User/Holon/ActionGraphBundle flip to
// SurrealDB now that their 090/100/110 .mermaid/.surql schemas + inline-POCO
// adapters have landed. Only Action remains on EF, gated on
// action-temporal-fork-model. Scoped lifetime in both cases.
builder.Services.AddScoped<IUserStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealUserStore>();
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
builder.Services.AddScoped<IActionGraphBundleStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionGraphBundleStore>();
// star-odk-ecosystem-tree (D2): tree of DappSeries/ActionGraphBundle nodes owned by an ActionGraphBundle.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IEcosystemStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealEcosystemStore>();
// kyc-module: KYC submission/document persistence (SurrealDB).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IKycStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealKycStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IKycControlStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealKycControlStore>();
// surrealdb-migration wave-2 round-3 close (residual task 9): IActionStore
// flips to the SurrealDB-backed adapter now that the definition-side
// schema files (150_action / 160_action_node / 170_action_edge) and the
// existing action_template / action_node_template tables (130 / 140) are
// all in place — actions now survive restart. The InMemoryActionStore
// class file remains on disk as a one-line revert path; only the DI
// binding is flipped here. Definition-side template reads continue to
// be served by SurrealActionTemplateStore via the separate
// IActionTemplateStore interface (CLOSEOUT Stream C2) — both shapes
// share the underlying action_template row format.
builder.Services.AddScoped<IActionStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionStore>();
builder.Services.AddScoped<INftStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNftStore>();
// D19 credential revocation reads the issuer / soulbound / holder-address columns
// of the SAME nft_ownership rows, through a narrower interface the same Surreal
// store implements. Bound explicitly rather than cast off INftStore so re-binding
// one seam can never silently change the other.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.ICredentialIssuerStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNftStore>();
builder.Services.AddScoped<IBridgeStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealBridgeStore>();
// surrealdb-migration CLOSEOUT Stream C2 (pre-D gap closure): ApiKey + Action
// template catalog flipped to per-aggregate Surreal stores. ApiKey: backs
// ApiKeyAuthenticationHandler + ApiKeyController. ActionTemplate: backs
// ActionInstantiator (definition-side catalog reads only — the runtime
// action_run / action_node_execution tables are owned by
// action-temporal-fork-model and remain on the InMemory adapter until that
// track lands).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IApiKeyStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealApiKeyStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IActionTemplateStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionTemplateStore>();

// ─── Data-backfill primitive (final-hardening-cutover Phase E2) ───
// Application-level data-rewrite runner + data_migration ledger. Distinct from
// surrealforge's schema_migration DDL ledger. IBackfill units are registered as
// scoped services (empty registry today — greenfield pre-launch has zero rows to
// rewrite); the runner discovers them via IEnumerable<IBackfill>. Surface:
// GET/POST /api/admin/backfill (Operator policy). See Services/Backfill/AGENTS.md.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IDataMigrationLedgerStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealDataMigrationLedgerStore>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Backfill.BackfillRunner>();

// <action-temporal-fork-model>
// Per-attempt runtime stores for ActionRun + ActionNodeExecution. As of
// surrealdb-migration wave-2 round-3 close (residual task 9) both flip to
// the SurrealDB-backed adapters — actions + per-(run, node) execution rows
// now survive restart. The schemas they bind to are
// 190_action_run.surql + 200_action_node_execution.surql, with the
// 230_action_graph_edges.surql RELATION tables (forked_from + executes)
// providing the cheap-graph-traversal lane for lineage walks. The
// InMemory* class files remain on disk so a future revert is a one-line
// DI swap.
builder.Services.AddScoped<IActionRunStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionRunStore>();
builder.Services.AddScoped<IActionNodeExecutionStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionNodeExecutionStore>();
// </action-temporal-fork-model>

// action-invitations-approval: request/approval flow store for invite-gated actions.
builder.Services.AddScoped<IActionAccessRequestStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionAccessRequestStore>();

// ─── user-self-sovereignty: consent + wallet-auth + webhook stores ───────────
// user-sovereign-identity: wallet-challenge auth nonce + tenant claim-invite token.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWalletAuthChallengeStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealWalletAuthChallengeStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IWalletAuthClaimTokenStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealWalletAuthClaimTokenStore>();
// Phase 1B federated sign-in: the (tenant_scope, provider, subject) binding and the
// single-use handoff code that carries an OAuth result to the SPA.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IUserExternalLoginStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealUserExternalLoginStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IOAuthExchangeCodeStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealOAuthExchangeCodeStore>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Auth.IExternalLoginService,
    AZOA.WebAPI.Services.Auth.ExternalLoginService>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Auth.IOAuthExchangeCodeService,
    AZOA.WebAPI.Services.Auth.OAuthExchangeCodeService>();
// The ONE login-token mint. Shared with the password path (UserManager.GenerateJwt
// delegates to the same implementation) so a federated session and a password session
// are the same credential, not two shapes that drift.
builder.Services.AddSingleton<AZOA.WebAPI.Services.Auth.IUserLoginTokenIssuer,
    AZOA.WebAPI.Services.Auth.UserLoginTokenIssuer>();
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
// final-hardening F3: generic action.emit webhook transactional outbox — a parallel
// outbox to the consent one that SHARES the registration store (above) + SSRF guard +
// HMAC signer + WebhookOptions (all registered on the consent path below).
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IActionWebhookOutboxStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealActionWebhookOutboxStore>();

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

builder.Services.AddScoped<IUserManager, UserManager>();
// final-hardening-cutover H2: operator:admin JWT-mint bootstrap. Stamping itself
// lives in UserManager.GenerateJwt (stateless, per-mint); this hosted service
// only makes a PARTIAL AdminBootstrap config loud at boot. See Services/Admin/AGENTS.md.
builder.Services.AddOptions<AZOA.WebAPI.Services.Admin.AdminBootstrapOptions>()
    .Bind(builder.Configuration.GetSection(AZOA.WebAPI.Services.Admin.AdminBootstrapOptions.SectionName));
builder.Services.AddOptions<AZOA.WebAPI.Services.Admin.NodeOperatorOptions>()
    .Bind(builder.Configuration.GetSection(AZOA.WebAPI.Services.Admin.NodeOperatorOptions.SectionName));
builder.Services.Configure<AZOA.WebAPI.Services.Admin.NodeOperatorLoginThrottleOptions>(
    builder.Configuration.GetSection("RateLimiting:OperatorLogin"));
builder.Services.AddSingleton<AZOA.WebAPI.Services.Admin.NodeOperatorLoginThrottle>();
builder.Services.AddHostedService<AZOA.WebAPI.Services.Admin.SeedAdminHostedService>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeOperatorManager,
    AZOA.WebAPI.Managers.NodeOperatorManager>();
builder.Services.AddSingleton<WalletKeyService>();
builder.Services.AddSingleton<IAlgorandFaucet, AlgorandFaucet>();
builder.Services.AddScoped<IWalletManager, WalletManager>();
builder.Services.AddScoped<ITenantCustodialAccountManager, TenantCustodialAccountManager>();
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
// final-hardening F3: the GENERIC action.emit webhook path. The Emit node's enqueue seam
// (ActionWebhookEmitter) + a named HttpClient using the SAME SSRF-guarded, no-auto-redirect
// primary handler as the consent client + the parallel hosted delivery worker. Reuses the
// shared WebhookSsrfGuard / WebhookHmacSigner / WebhookOptions / IWebhookRegistrationStore
// registered above — a tenant's ONE registration receives both consent and action.emit events.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IActionWebhookEmitter,
    AZOA.WebAPI.Services.Actions.ActionWebhookEmitter>();
builder.Services.AddHttpClient(AZOA.WebAPI.Services.Webhooks.WebhookOptions.ActionHttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = AZOA.WebAPI.Core.Webhooks.WebhookSsrfGuard.CreateGuardedConnectCallback(),
    });
builder.Services.AddHostedService<AZOA.WebAPI.Services.Webhooks.ActionWebhookDeliveryWorker>();
// KYC selection is explicit and fail-closed. Unknown values never inherit the
// manual provider; hosted and Veriff remain unavailable until reviewed adapters land.
builder.Services.Configure<AZOA.WebAPI.Settings.KycSettings>(
    builder.Configuration.GetSection(AZOA.WebAPI.Settings.KycSettings.SectionName));
builder.Services.AddScoped<AZOA.WebAPI.Providers.Kyc.ManualKycProviderService>();
builder.Services.AddScoped<AZOA.WebAPI.Providers.Kyc.VeriffKycProviderService>();
builder.Services.AddScoped<AZOA.WebAPI.Providers.Kyc.GenericHostedKycProviderService>();
// ─── Identomat hosted KYC + AML screening ───
// The named client has NO base address: the adapter genuinely spans two hosts
// (external-api for /begin and screening, widget for the authoritative /result/),
// so each call supplies its own absolute origin from configuration.
builder.Services.AddHttpClient(AZOA.WebAPI.Settings.IdentomatKycSettings.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        // A redirect on an identity call would silently move a company key to
        // another origin; never follow one.
        AllowAutoRedirect = false,
    });
// Singletons: the shared per-IP budget and the per-tenant quota are node-wide
// ceilings. Scoping them per request would make each of them count to one.
builder.Services.AddSingleton<AZOA.WebAPI.Services.Kyc.IIdentomatRateBudget,
    AZOA.WebAPI.Services.Kyc.IdentomatRateBudget>();
builder.Services.AddSingleton<AZOA.WebAPI.Services.Kyc.IKycTenantQuota,
    AZOA.WebAPI.Services.Kyc.KycTenantQuota>();
builder.Services.AddSingleton<AZOA.WebAPI.Services.Kyc.IKycPersonKeyDeriver,
    AZOA.WebAPI.Services.Kyc.KycPersonKeyDeriver>();
builder.Services.AddSingleton<AZOA.WebAPI.Services.Kyc.IKycScreeningRiskPolicy,
    AZOA.WebAPI.Services.Kyc.KycScreeningRiskPolicy>();
builder.Services.AddScoped<AZOA.WebAPI.Providers.Kyc.IIdentomatHttpTransport,
    AZOA.WebAPI.Providers.Kyc.IdentomatHttpTransport>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.HostedSessions.IHostedSessionStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealHostedSessionStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IKycIdentityStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealKycIdentityStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IKycScreeningStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealKycScreeningStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IPersonOperationClaimStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealPersonOperationClaimStore>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Kyc.IKycIdentityBindingService,
    AZOA.WebAPI.Services.Kyc.KycIdentityBindingService>();
// D3/D23 per-operation one-human-one-claim gate. An operation opts in by declaring
// a PersonUniquenessRequirement; without this registration the seam is inert and a
// declaring operation would fail to resolve its gate at all.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IPersonUniquenessGate,
    AZOA.WebAPI.Services.Kyc.PersonUniquenessGate>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Kyc.IKycIdentityErasureService,
    AZOA.WebAPI.Services.Kyc.KycIdentityErasureService>();
builder.Services.AddScoped<AZOA.WebAPI.Services.HostedSessions.IHostedSessionOutcomeHandler,
    AZOA.WebAPI.Services.Kyc.KycHostedSessionOutcomeHandler>();
builder.Services.AddScoped<AZOA.WebAPI.Services.HostedSessions.IHostedSessionFlow,
    AZOA.WebAPI.Services.HostedSessions.HostedSessionFlow>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Kyc.IIdentomatScreeningService,
    AZOA.WebAPI.Services.Kyc.IdentomatScreeningService>();
builder.Services.AddScoped<AZOA.WebAPI.Providers.Kyc.IdentomatKycProviderService>();
// Cold poll for AML ongoing monitoring. There is NO push channel for monitoring at
// all, so this worker is the only change-detection path; it self-disables unless
// Kyc:Identomat:Screening:MonitorPollEnabled is set.
builder.Services.AddHostedService<AZOA.WebAPI.Services.Kyc.KycScreeningMonitorPollWorker>();
// PII retention/erasure sweep. Without this registration the retention window is
// decorative: nothing ever expires a KYC identity record (GDPR Art. 17 vs the
// KYC record-keeping obligation). Owned by the KYC lane; registered here because
// Program.cs is single-writer.
builder.Services.AddHostedService<AZOA.WebAPI.Services.Kyc.KycIdentityRetentionSweepWorker>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderRegistry,
    AZOA.WebAPI.Services.Kyc.KycProviderRegistry>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IKycControlPlaneManager,
    AZOA.WebAPI.Managers.KycControlPlaneManager>();
var configuredKycProvider = builder.Configuration[
    $"{AZOA.WebAPI.Settings.KycSettings.SectionName}:Provider"]?.Trim().ToLowerInvariant();
switch (configuredKycProvider)
{
    case null:
    case "":
    case "manual":
        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService,
                AZOA.WebAPI.Providers.Kyc.ManualKycProviderService>();
        }
        else
        {
            builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService,
                AZOA.WebAPI.Providers.Kyc.UnavailableKycProviderService>();
        }
        break;
    case "veriff":
        builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService,
            AZOA.WebAPI.Providers.Kyc.VeriffKycProviderService>();
        break;
    case "hosted":
    case "generic-hosted":
        builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService,
            AZOA.WebAPI.Providers.Kyc.GenericHostedKycProviderService>();
        break;
    case AZOA.WebAPI.Providers.Kyc.IdentomatKycProviderService.ProviderKeyValue:
        builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService>(sp =>
            sp.GetRequiredService<AZOA.WebAPI.Providers.Kyc.IdentomatKycProviderService>());
        break;
    default:
        builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Providers.IKycProviderService,
            AZOA.WebAPI.Providers.Kyc.UnavailableKycProviderService>();
        break;
}
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IKycManager,
    AZOA.WebAPI.Managers.KycManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IKycGateService,
    AZOA.WebAPI.Services.Kyc.KycGateService>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IValueAccessService,
    AZOA.WebAPI.Services.Kyc.ValueAccessService>();
builder.Services.Configure<AZOA.WebAPI.Services.Governance.NodeGovernanceOptions>(
    builder.Configuration.GetSection(AZOA.WebAPI.Services.Governance.NodeGovernanceOptions.SectionName));
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.INodeGovernanceGuard,
    AZOA.WebAPI.Services.Governance.NodeGovernanceGuard>();
// fiat-stripe-bridge: idempotent, KYC-gated, tenant-callable allocation primitive.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IAllocationManager,
    AZOA.WebAPI.Managers.AllocationManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IAllocationReceiptManager,
    AZOA.WebAPI.Managers.AllocationReceiptManager>();
// fungible-token-node: idempotent, KYC-gated fungible-token (ASA) launch seam.
// Registered by CONCRETE type first, with the interface forwarding to the same scoped
// instance. The concrete class carries the mint-destination-explicit CreateAsync overload
// (W3.1) that platform-token genesis needs; IFungibleTokenManager only exposes the
// config-resolved overload, and a second AddScoped for the interface would hand callers
// two different instances inside one request.
builder.Services.AddScoped<AZOA.WebAPI.Managers.FungibleTokenManager>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IFungibleTokenManager>(
    sp => sp.GetRequiredService<AZOA.WebAPI.Managers.FungibleTokenManager>());
// D19 soulbound-credential revocation (Algorand AssetClawback). Backs both
// CredentialController and CredentialRevokeNodeHandler -- and the handler set is
// constructed EAGERLY (ActionNodeHandlerRegistry takes IEnumerable<IActionNodeHandler>
// and the scan in the "Action node handlers" block registers every one of them), so a
// missing registration here is a startup crash for the whole app, not a lazy 500.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.ICredentialManager,
    AZOA.WebAPI.Managers.CredentialManager>();
// D16 analytics read surface -- a read-only projection over the blockchain-operation
// log that backs AnalyticsController. No writes, no chain reach.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IAnalyticsReadService,
    AZOA.WebAPI.Services.Analytics.AnalyticsReadService>();
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
builder.Services.AddScoped<IActionGraphBundleManager, ActionGraphBundleManager>();
builder.Services.AddScoped<INftManager, NftManager>();
builder.Services.AddScoped<ISearchManager, SearchManager>();
builder.Services.AddScoped<IUserNFTService, UserNFTService>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Actions.ActionConfigBindingResolver>();
builder.Services.AddScoped<IActionManager, ActionManager>();

// <dapp-composition>
// IDappSeriesStore + IDappCompositionManager are the dapp-composition track's
// surfaces. The store operates on source-gen'd DappSeries + DappSeriesAction
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
// Ethereum signer: real secp256k1 signing over the canonical EIP-1559 payload.
// See Providers/Blockchain/Ethereum/AGENTS.md §signer for the canonical byte contract.
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.Signing.ITransactionSigner,
    AZOA.WebAPI.Providers.Blockchain.Ethereum.EthereumTransactionSigner>();
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
// Ethereum: a BARE AddTransient on purpose. Constructor selection then picks the 4-arg
// overload whose every parameter is a root-resolvable singleton (config, logger, signer
// factory, scope factory), so the value path gets a real signer. A factory lambda would
// invite passing IKeyCustodyService directly — it is SCOPED, while the provider factory
// caches one provider per chain/network for the app lifetime, which would make it a
// captive dependency. EthereumProviderPipelineTests pins this selection.
builder.Services.AddTransient<AZOA.WebAPI.Providers.Blockchain.Ethereum.EthereumProvider>();
builder.Services.AddTransient<AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider>();

// Phase 0 capability-honesty guard: keep this list in sync with the ChainType values
// registered in the BlockchainProviderRegistration array immediately below. Fails boot
// if Blockchain:Chains config enables a chain (e.g. "Ethereum") with no registered
// provider, instead of letting it fail lazily at request time.
var registeredBlockchainChainTypes = new[] { "Algorand", "Solana", "Ethereum", "Simulated" };
BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains(
    builder.Configuration, registeredBlockchainChainTypes);

builder.Services.AddSingleton<IBlockchainProviderFactory>(sp =>
    new BlockchainProviderFactory(
        new[]
        {
            new BlockchainProviderRegistration("Algorand", sp.GetRequiredService<AlgorandProvider>),
            new BlockchainProviderRegistration("Solana", sp.GetRequiredService<SolanaProvider>),
            // Returns an UNBOUND provider: the factory owns the network binding and a
            // pre-initialized instance is rejected (BaseBlockchainProvider.InitializeForFactory).
            new BlockchainProviderRegistration(
                "Ethereum",
                sp.GetRequiredService<AZOA.WebAPI.Providers.Blockchain.Ethereum.EthereumProvider>),
            new BlockchainProviderRegistration(
                "Simulated",
                sp.GetRequiredService<AZOA.WebAPI.Providers.Blockchain.Simulated.SimulatedBlockchainProvider>),
            // ─── Fiat.USD (W4.1) — a NON-chain rail on the same factory ───
            // It brings its OWN environment → endpoint resolver, because Blockchain:Chains is
            // keyed Devnet/Testnet/Mainnet and a card rail has test and live only. A null
            // result from that resolver is how "Fiat:Enabled=false", "that is not the selected
            // environment" and "the secret is missing" all collapse into one fail-closed
            // answer: the factory reports the environment disabled and no rail is handed out.
            // Registered here rather than in a sibling extension because the factory takes its
            // whole registration set in one constructor call.
            new ValueRailProviderRegistration(
                AZOA.WebAPI.Providers.Fiat.FiatRailIds.UsdRail,
                sp.GetRequiredService<AZOA.WebAPI.Providers.Fiat.FiatRailProvider>,
                environment => sp.GetRequiredService<AZOA.WebAPI.Providers.Fiat.FiatOptions>()
                    .ResolveEndpoint(environment)),
        },
        sp.GetRequiredService<IConfiguration>()));

// The rail-generic face of the SAME singleton. Without this line the D9 seam is unreachable
// from DI: AddSingleton<IBlockchainProviderFactory> registers exactly ONE service type, so a
// consumer that correctly depends on IValueRailProviderFactory — because it settles on a rail
// that is not a chain — cannot be constructed at all. Forwarding rather than registering a
// second instance is what keeps ONE provider cache; two caches would mean two bound instances
// per (rail, environment), and for a payment rail that is two live bindings of one environment.
builder.Services.AddSingleton<IValueRailProviderFactory>(sp =>
    sp.GetRequiredService<IBlockchainProviderFactory>());

// ─── Platform reserve (W3.1) ───
// The operator-owned custodial account every unit a sponsor or a worker receives comes
// FROM: it holds the platform unit and every project asset, is the manager/reserve/
// freeze/clawback authority on the assets this node creates, and pays the chain fees.
// Its key is the per-chain platform key behind IKeyCustodyService — the reserve has no
// secret of its own, so a chain with no configured platform key resolves NO reserve
// address rather than one this node cannot sign for.
//
// ReserveOptions is registered as a bound INSTANCE (not IOptions<T>) because the reserve
// and the fungible-token manager read it as plain configuration, and because a null
// section must still yield the fail-closed defaults rather than an unbound object.
var reserveOptions = builder.Configuration
    .GetSection(AZOA.WebAPI.Services.Reserve.ReserveOptions.SectionName)
    .Get<AZOA.WebAPI.Services.Reserve.ReserveOptions>()
    ?? new AZOA.WebAPI.Services.Reserve.ReserveOptions();
builder.Services.AddSingleton(reserveOptions);
builder.Services.AddScoped<AZOA.WebAPI.Services.Reserve.IPlatformReserveLedgerStore,
    AZOA.WebAPI.Services.Reserve.SurrealPlatformReserveLedgerStore>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Reserve.IPlatformReserve,
    AZOA.WebAPI.Services.Reserve.PlatformReserveService>();

// ─── Wave 3 composition (each item owns its own *Registration.cs; W3.1 owns this file) ───
// CONTRACT for the sibling items — the exact symbols this file calls. An item that names
// its extension differently breaks the build here, which is deliberate: an unregistered
// value service that boots green is how a feature ships inert.
//   W3.2  AZOA.WebAPI.Services.Reserve.PlatformTokenGenesisRegistration.AddPlatformTokenGenesis(IServiceCollection)
//   W3.3  AZOA.WebAPI.Services.Allocation.AllocationIssuanceRegistration.AddAllocationIssuance(IServiceCollection)
//   W3.4  AZOA.WebAPI.Services.Hold.HoldRegistration.AddPlatformHold(IServiceCollection)
//   W3.6  AZOA.WebAPI.Services.Consent.TenantProvisionedGrantRegistration
//           .AddTenantProvisionedConsentGrants(IServiceCollection)   [landed — name verified]
//   W3.7  AZOA.WebAPI.Services.Reserve.ProjectTreasuryRegistration.AddProjectTreasury(IServiceCollection)
// Each is called as a fully-qualified static (the AddCrossChainBridgeProtocols precedent)
// so adding one cannot introduce a name ambiguity elsewhere in this file.
// W3.2 sits first: genesis mints THROUGH the reserve registrations above and through the
// concrete FungibleTokenManager registered earlier in this file, and everything downstream
// of it spends the supply it creates.
AZOA.WebAPI.Services.Reserve.PlatformTokenGenesisRegistration
    .AddPlatformTokenGenesis(builder.Services);
AZOA.WebAPI.Services.Allocation.AllocationIssuanceRegistration
    .AddAllocationIssuance(builder.Services);
AZOA.WebAPI.Services.Hold.HoldRegistration
    .AddPlatformHold(builder.Services);
// W3.6 throws unless IConsentGrantStore (line ~1004) and IConsentManager (line ~1111) are
// already registered, which is why this call sits here and not earlier in the file.
AZOA.WebAPI.Services.Consent.TenantProvisionedGrantRegistration
    .AddTenantProvisionedConsentGrants(builder.Services);
AZOA.WebAPI.Services.Reserve.ProjectTreasuryRegistration
    .AddProjectTreasury(builder.Services);

// ─── Fiat.USD rail (W4.1) ───
// The card rail: money in through a hosted payment page today, money out from Wave 8. It is a
// value rail, not a chain, and it is registered through the rail-aware factory above.
//
// FiatOptions is bound as an INSTANCE (the ReserveOptions precedent): an absent section must
// still yield the fail-closed defaults rather than an unbound object, and the rail, both
// controllers and the webhook inbox must all read the SAME environment selection. Two call
// sites each picking their own test-vs-live answer is a test-mode checkout settled by a live
// webhook — which is why nothing below re-reads Fiat:Environment for itself.
var fiatOptions = builder.Configuration
    .GetSection(AZOA.WebAPI.Providers.Fiat.FiatOptions.SectionName)
    .Get<AZOA.WebAPI.Providers.Fiat.FiatOptions>()
    ?? new AZOA.WebAPI.Providers.Fiat.FiatOptions();
builder.Services.AddSingleton(fiatOptions);

// Named client: its own connection pool and handler lifetime. The per-request deadline is the
// rail's (linked CancellationTokenSource), so no HttpClient.Timeout is set here — mutating a
// pooled client's Timeout per call is a race.
builder.Services.AddHttpClient(AZOA.WebAPI.Providers.Fiat.StripeGateway.HttpClientName);
builder.Services.AddSingleton<AZOA.WebAPI.Providers.Fiat.IStripeGateway,
    AZOA.WebAPI.Providers.Fiat.StripeGateway>();

// Transient on purpose: the factory caches one BOUND instance per (rail, environment) and a
// pre-bound instance is refused, exactly as for a chain provider.
builder.Services.AddTransient<AZOA.WebAPI.Providers.Fiat.FiatRailProvider>();

builder.Services.AddScoped<AZOA.WebAPI.Services.Fiat.IStripeWebhookInbox,
    AZOA.WebAPI.Services.Fiat.StripeWebhookInbox>();

// Intent bookkeeping: it moves the funding intent through its states and credits NOTHING, which
// is why it implements IFiatEventHandler and NOT IFiatSettlementHandler. That distinction is
// load-bearing — the live-environment boot guard below counts settlement handlers, so this
// registration can never be mistaken for a wired money path.
builder.Services.AddScoped<AZOA.WebAPI.Services.Fiat.IFiatEventHandler,
    AZOA.WebAPI.Services.Fiat.FiatCheckoutIntentEventHandler>();

// ─── Wave 4 composition (W4.1 owns this file for the wave) ───
// W4.6 — the D16 value-rate seam. Without this call the tenant-declared rate source is INERT
// and every operation settles unpriced; the extension is TryAdd-based, so a durable store
// registered before it still wins.
AZOA.WebAPI.Services.Analytics.AnalyticsRegistration
    .AddValueRateSources(builder.Services, builder.Configuration);
//
// W4.5 needs no new registration: IReconciliationService and ReconciliationHostedService are
// already registered further down this file, and that item extends them in place.
//
// CONTRACT for the settlement items. W4.2 (on-ramp) and W4.3 (reversal pipeline) do not own
// this file, and the plan gave them no registration call, so the wave's join gate wired them.
// They are NOT registered here: both extensions CHECK their collaborators rather than trusting
// call order, and two of those collaborators (IRealValueKycGate, ISagaCoordinator) are
// registered further down this file. The four calls therefore sit in the
// "Wave 4 settlement composition" block below the saga layer. Until they existed the on-ramp
// and the reversal ladder compiled, tested and shipped INERT.
//
// The gap is also closed by teeth rather than by prose: FiatRailStartup.Validate below REFUSES
// to boot the live environment unless an IFiatSettlementHandler is registered, so a node cannot
// charge a real card while nothing is able to credit it.

// The matching boot guard runs immediately before builder.Build(), NOT here: it counts the
// registrations in the container, so running it mid-file would read a snapshot taken before a
// later item added its handler and pass on a node that cannot credit anything.

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

// Receipt reconciliation is a scoped, explicitly invoked observation seam. It is
// deliberately not a hosted worker or fee-consumer activation path.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.INodeFeeSettlementStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealNodeFeeSettlementStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.INodeFeeSettlementAtomicGroupReconciler,
    AZOA.WebAPI.Services.Governance.NodeFeeSettlementAtomicGroupReconciler>();

// ─── Cross-chain bridge (hybrid trusted + Wormhole) ───
// surrealdb-migration wave-2 task 8: routes through IBridgeStore +
// IIdempotencyStore. Storage backend is SurrealDB after wave-3 EF deletion.
// bridge-safety-hardening Phase A: kill-switch options (RealValueEnabled defaults false).
builder.Services.AddOptions<AZOA.WebAPI.Services.Bridge.BridgeOptions>()
    .Bind(builder.Configuration.GetSection(
        AZOA.WebAPI.Services.Bridge.BridgeOptions.SectionName));
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IRealValueKycGate,
    AZOA.WebAPI.Services.Bridge.RealValueKycGate>();
// CrossChainBridgeService takes IBridgeProtocol<WormholeVAA>; this registers the
// protocol implementations that satisfy it. Registered by request of the bridge
// agent, which owns the extension itself (Services/Bridge/BridgeProtocolServiceCollectionExtensions.cs).
// MUST precede the ICrossChainBridgeService registration's first resolution — without
// it the host builds and the unit suite stays green (those tests construct the service
// directly) but DI resolution fails the first time a bridge request arrives.
// Called as a plain static rather than via `using AZOA.WebAPI.Services.Bridge;` so the
// one-line addition cannot introduce a name ambiguity elsewhere in this file.
AZOA.WebAPI.Services.Bridge.BridgeProtocolServiceCollectionExtensions
    .AddCrossChainBridgeProtocols(builder.Services);
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
// The FIRST real saga consumer: a single "action-workflow" ISagaDefinition whose
// type-uniform FindStep resolves every node-id step to one self-advancing node
// handler (Approach A). The definition is pure metadata (Singleton); the step
// handlers wrap scoped action stores (Scoped). The two handlers close over
// DISTINCT payload types so GetRequiredService<IStepHandler<T>> is unambiguous.
builder.Services.AddSingleton<AZOA.WebAPI.Sagas.ISagaDefinition,
    AZOA.WebAPI.Services.Actions.Workflow.ActionWorkflowSagaDefinition>();
builder.Services.AddScoped<
    AZOA.WebAPI.Sagas.IStepHandler<AZOA.WebAPI.Services.Actions.Workflow.ActionStepPayload>,
    AZOA.WebAPI.Services.Actions.Workflow.ActionNodeStepHandler>();
builder.Services.AddScoped<
    AZOA.WebAPI.Sagas.IStepHandler<AZOA.WebAPI.Services.Actions.Workflow.ActionCompensatePayload>,
    AZOA.WebAPI.Services.Actions.Workflow.ActionCompensateStepHandler>();

// ─── Wave 4 settlement composition (added by the wave's join gate) ───
// The money path the contract comment above asked for, placed HERE and not in the Wave 4 block
// because every one of these extensions VERIFIES its collaborators and throws at boot instead
// of registering something that cannot work:
//   AddFiatOnRamp             needs ISurrealExecutor, IAllocationManager, IPlatformTokenGenesis
//                             and IRealValueKycGate (registered ~line 1599).
//   AddOnRampValueRateSource  needs AddFiatOnRamp AND AddValueRateSources (~line 1539).
//   AddFiatReversalPipeline   needs the on-ramp lot ledger, the reserve, the hold primitive,
//                             the idempotency store, ISagaCoordinator (~line 1640), the
//                             provider factory, IWalletStore and the named Stripe client.
//   AddReversalRiskValueGate  needs AddFiatReversalPipeline AND the D46 gate it wraps.
// Ordering inside the block is therefore load-bearing and is asserted by those guards.
AZOA.WebAPI.Services.Fiat.OnRamp.OnRampRegistration
    .AddFiatOnRamp(builder.Services);
// Prices the D16 value stamp from the SAME schedule that converted the money. It REPLACES the
// configuration-backed declared-rate store installed by AddValueRateSources and composes it as
// the fallback, so one price decides both the quantity issued and what the row was worth.
AZOA.WebAPI.Services.Fiat.OnRamp.OnRampRegistration
    .AddOnRampValueRateSource(builder.Services);
// Refunds, disputes and the three-rung recovery ladder. Registers IFiatEventHandler ONLY: it
// credits nothing, so it must never satisfy the live-boot settlement probe below.
AZOA.WebAPI.Services.Fiat.Reversal.ReversalRegistration
    .AddFiatReversalPipeline(builder.Services);
// Makes the account risk flag a CONTROL instead of a written-and-never-read field: it wraps the
// D46 value gate so an account with an unresolved reversal cannot buy more units or move value
// out. Without this call the flag is written and nothing reads it.
AZOA.WebAPI.Services.Fiat.Reversal.ReversalRegistration
    .AddReversalRiskValueGate(builder.Services);

// ─── Wave 5 composition: the treasury-rate exchange (W5.1 / N-X1 / D50) ───
// The sponsorship moment. A sponsor spends the platform unit, the PROJECT receives it on chain
// (never the platform reserve), and the sponsor's units either arrive or are held for a window —
// all in one atomic group. It is composed from primitives registered above, so it sits here and
// not in the Wave 3/4 blocks: it needs the reserve, the project treasury, the hold primitive,
// platform-unit genesis, the recipient preparer, the idempotency store and the D16 rate seam.
//
// This is NOT a swap. The existing Swap node and SwapManager stay on the DEX path; nothing below
// touches ISwapManager.
var exchangeOptions = builder.Configuration
    .GetSection(AZOA.WebAPI.Services.Reserve.ExchangeOptions.SectionName)
    .Get<AZOA.WebAPI.Services.Reserve.ExchangeOptions>()
    ?? new AZOA.WebAPI.Services.Reserve.ExchangeOptions();
builder.Services.AddSingleton(exchangeOptions);

// The rate policy. It admits the rate the consumer declared and REFUSES the reverse direction
// while no IExchangeLiquidityPolicy is registered — which is the state of this node today, and
// is the whole of "users cannot sell yet" (W8.2 registers the policy that changes it). Nothing
// registers IExchangeLiquidityPolicy below, deliberately; the manager reads the refusal.
builder.Services.AddScoped<AZOA.WebAPI.Services.Reserve.IExchangeRatePolicy,
    AZOA.WebAPI.Services.Reserve.TreasuryRateExchangePolicy>();

// The atomic-group submitter. The fail-closed default settles NOTHING: no rail adapter can yet
// assemble a two-asset, two-signer, fee-pooled group (IAtomicTransferGroupModule carries only a
// same-asset, same-signer pair). TryAdd, so a real adapter registered earlier wins; without one
// every exchange refuses rather than degrading into two separate transfers.
Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
    .TryAddScoped<AZOA.WebAPI.Services.Reserve.IExchangeGroupSubmitter,
        AZOA.WebAPI.Services.Reserve.UnavailableExchangeGroupSubmitter>(builder.Services);

// The durable declared-rate store, composing whatever AddValueRateSources / AddOnRampValueRateSource
// left in place as the fallback. It must run AFTER both, or the on-ramp's price schedule — which
// prices the platform unit it sold — would be dropped instead of composed.
AZOA.WebAPI.Services.Reserve.HolonTenantDeclaredRateStore.Install(builder.Services);

builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IReserveExchangeManager,
    AZOA.WebAPI.Managers.ReserveExchangeManager>();

// ─── MCP surface (mcp-surface track Phase 1) ───
// Registers McpToolRegistry (singleton) + the SDK's Streamable HTTP transport
// at /mcp. Tool implementations (W2-W5) register themselves as IMcpTool via DI;
// McpToolRegistry's ctor enumerates them at first resolution.
builder.Services.AddMcpSurface();

// ─── Action DAG system ───
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IActionDagValidator, AZOA.WebAPI.Services.ActionDagValidator>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IActionDagExecutabilityValidator, AZOA.WebAPI.Services.Actions.ActionDagExecutabilityValidator>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.IActionInstantiator, AZOA.WebAPI.Services.Actions.ActionInstantiator>();

// Action node handlers — exactly one per ActionNodeType. Registered Scoped: each
// handler wraps scoped managers, so Singleton would capture a disposed scope
// (captive-dependency bug). Discovered by assembly scan over the single sealed
// Services/Actions/Handlers namespace so adding a handler needs no DI edit; the
// registry's ctor still fails fast on a duplicate/missing ActionNodeType.
foreach (var handlerType in typeof(ActionNodeHandlerRegistry).Assembly
             .GetTypes()
             .Where(t => t is { IsClass: true, IsAbstract: false }
                         && t.Namespace == "AZOA.WebAPI.Services.Actions.Handlers"
                         && typeof(IActionNodeHandler).IsAssignableFrom(t)))
{
    builder.Services.AddScoped(typeof(IActionNodeHandler), handlerType);
}
builder.Services.AddScoped<IActionNodeHandlerRegistry, ActionNodeHandlerRegistry>();

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

// ─── Fiat rail boot guard (W4.1) ───
// Capability honesty for the money path, mirroring the two chain guards in
// BlockchainProviderFactory: a node that advertises a payment rail it cannot complete must fail
// at STARTUP, not at the moment a person's card is charged. It runs here, after every
// registration, because it counts what is actually in the container — the live environment is
// refused unless something implementing IFiatSettlementHandler can turn a cleared payment into
// funds in the payer's account.
AZOA.WebAPI.Providers.Fiat.FiatRailStartup.Validate(
    fiatOptions,
    simulatedBlockchainMode: string.Equals(
        builder.Configuration.GetSection("Blockchain").Get<BlockchainConfig>()?.Mode ?? "Live",
        "Simulated",
        StringComparison.OrdinalIgnoreCase),
    settlementHandlerRegistered: builder.Services.Any(descriptor =>
        descriptor.ServiceType == typeof(AZOA.WebAPI.Services.Fiat.IFiatSettlementHandler)
        || descriptor.ImplementationType is not null
            && typeof(AZOA.WebAPI.Services.Fiat.IFiatSettlementHandler)
                .IsAssignableFrom(descriptor.ImplementationType)));

var app = builder.Build();

// Federated sign-in post-build guard (Phase 1B D18). The pre-build guard above proves
// the SECRETS are present; this proves the REGISTRATIONS are, by resolving each service
// once and confirming every advertised provider has an authentication scheme. A
// missing DI registration otherwise builds clean, passes the unit suite (those tests
// construct types directly), and fails only on the first real sign-in.
AZOA.WebAPI.Services.Auth.ExternalAuthRuntimeSafety.GuardRegistrationsResolve(
    app.Services, externalAuthOptions);

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
// authenticated user/user when no X-Api-Key header is present.
app.UseRateLimiter();
// W5 request correlation: after UseRouting, before MapControllers — attaches
// the W3C TraceId/SpanId as a structured log scope for every request.
app.UseAzoaRequestCorrelation();
// MCP surface (mcp-surface track): /mcp endpoint protected by the existing
// JWT+ApiKey multi-scheme via RequireAuthorization() inside MapMcp().
// UseMcpAuth (Phase 2 W4) extracts the UserId claim into ctx.Items so the
// MCP dispatcher can lift it into ToolCallContext for per-tool user scoping.
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
