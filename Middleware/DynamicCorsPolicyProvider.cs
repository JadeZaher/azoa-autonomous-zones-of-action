using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AZOA.WebAPI.Interfaces.Stores;

namespace AZOA.WebAPI.Middleware
{
    /// <summary>
    /// Dynamic CORS policy provider that resolves allowed origins per request type (API Key vs JWT dashboard).
    /// </summary>
    public class DynamicCorsPolicyProvider : ICorsPolicyProvider
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHostEnvironment _env;
        private readonly IConfiguration _config;

        public DynamicCorsPolicyProvider(
            IServiceScopeFactory scopeFactory,
            IHostEnvironment env,
            IConfiguration config)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _env = env ?? throw new ArgumentNullException(nameof(env));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Resolves the CORS policy dynamically for the incoming request based on the request type:
        /// <para>
        /// 1. Preflight OPTIONS requests dynamically allow the incoming request's Origin so the browser 
        /// can proceed to the actual request (where CORS headers and custom authentication headers are validated).
        /// </para>
        /// <para>
        /// 2. API Key requests validate the origin against the key's specific AllowedOrigins configuration in the database.
        /// If the allowed origins list is empty or null, any origin is permitted (*) to support frictionless public SDK usage.
        /// </para>
        /// <para>
        /// 3. Dashboard, JWT, and Login requests validate the origin against the node's configured Cors:AllowedOrigins in Production,
        /// or permit any origin in Development/IntegrationTest environments.
        /// </para>
        /// </summary>
        public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
        {
            var requestOrigin = context.Request.Headers["Origin"].FirstOrDefault();

            var policy = new CorsPolicy();
            policy.Headers.Add("*");
            policy.Methods.Add("*");

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                if (!string.IsNullOrEmpty(requestOrigin))
                {
                    policy.Origins.Add(requestOrigin);
                    policy.SupportsCredentials = true;
                }
                else
                {
                    policy.Origins.Add("*");
                }
                return policy;
            }

            string? apiKeyHeader = null;
            if (context.Request.Headers.TryGetValue("X-Api-Key", out var values))
            {
                apiKeyHeader = values.FirstOrDefault();
            }

            if (!string.IsNullOrWhiteSpace(apiKeyHeader))
            {
                var keyHash = Services.Auth.ApiKeyAuthenticationHandler.HashKey(apiKeyHeader);

                using (var scope = _scopeFactory.CreateScope())
                {
                    var store = scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
                    var apiKey = await store.GetByHashAsync(keyHash, context.RequestAborted);

                    if (apiKey != null && apiKey.IsActive && !apiKey.RevokedAt.HasValue && (!apiKey.ExpiresAt.HasValue || apiKey.ExpiresAt.Value > DateTime.UtcNow))
                    {
                        if (string.IsNullOrWhiteSpace(apiKey.AllowedOrigins))
                        {
                            if (!string.IsNullOrEmpty(requestOrigin))
                            {
                                policy.Origins.Add(requestOrigin);
                                policy.SupportsCredentials = true;
                            }
                            else
                            {
                                policy.Origins.Add("*");
                            }
                        }
                        else
                        {
                            var allowedOrigins = apiKey.AllowedOrigins
                                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                            if (allowedOrigins.Contains("*"))
                            {
                                if (!string.IsNullOrEmpty(requestOrigin))
                                {
                                    policy.Origins.Add(requestOrigin);
                                    policy.SupportsCredentials = true;
                                }
                                else
                                {
                                    policy.Origins.Add("*");
                                }
                            }
                            else if (!string.IsNullOrEmpty(requestOrigin) && allowedOrigins.Contains(requestOrigin, StringComparer.OrdinalIgnoreCase))
                            {
                                policy.Origins.Add(requestOrigin);
                                policy.SupportsCredentials = true;
                            }
                        }
                        return policy;
                    }
                }
            }

            if (_env.IsDevelopment() || _env.IsEnvironment("IntegrationTest"))
            {
                if (!string.IsNullOrEmpty(requestOrigin))
                {
                    policy.Origins.Add(requestOrigin);
                    policy.SupportsCredentials = true;
                }
                else
                {
                    policy.Origins.Add("*");
                }
            }
            else
            {
                var origins = _config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
                if (origins.Length > 0 && !string.IsNullOrEmpty(requestOrigin))
                {
                    if (origins.Contains(requestOrigin, StringComparer.OrdinalIgnoreCase))
                    {
                        policy.Origins.Add(requestOrigin);
                        policy.SupportsCredentials = true;
                    }
                }
            }

            return policy;
        }
    }
}
