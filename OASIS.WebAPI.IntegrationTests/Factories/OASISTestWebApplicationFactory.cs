using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Providers;

namespace OASIS.WebAPI.IntegrationTests.Factories;

/// <summary>
/// Custom WebApplicationFactory that swaps out production infrastructure
/// for test-friendly equivalents:
/// - InMemory EF database (instead of PostgreSQL)
/// - Test authentication scheme (instead of JWT)
/// - Exposes HTTP client pre-configured with auth header
/// </summary>
public class OASISTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"OASIS_TestDb_{Guid.NewGuid():N}";
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "super-secret-test-key-that-is-long-enough!",
                ["Jwt:Issuer"] = "test",
                ["Jwt:Audience"] = "test",
                ["OASIS:DefaultProvider"] = "PostgreSQL"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove real DbContext registration
            var dbDescriptor = services
                .SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<OASISDbContext>));
            if (dbDescriptor != null) services.Remove(dbDescriptor);

            // Add InMemory EF with unique DB per factory instance for test isolation
            services.AddDbContext<OASISDbContext>(options =>
                options.UseInMemoryDatabase(_dbName));

            // Remove real auth and add test auth
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                TestAuthHandler.SchemeName, _ => { });

            // Ensure only EfStorageProvider is used for consistent data access
            var inMemoryProvider = services.SingleOrDefault(d => d.ImplementationType == typeof(InMemoryStorageProvider));
            if (inMemoryProvider != null) services.Remove(inMemoryProvider);
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthHeaderName, "true");
        return client;
    }
}
