using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Middleware;
using AZOA.WebAPI.Models;

namespace AZOA.WebAPI.Tests.Middleware
{
    public class DynamicCorsPolicyProviderTests
    {
        private readonly Mock<IServiceScopeFactory> _scopeFactory;
        private readonly Mock<IServiceScope> _scope;
        private readonly Mock<IServiceProvider> _serviceProvider;
        private readonly Mock<IApiKeyStore> _apiKeyStore;
        private readonly Mock<IHostEnvironment> _environment;

        public DynamicCorsPolicyProviderTests()
        {
            _scopeFactory = new Mock<IServiceScopeFactory>();
            _scope = new Mock<IServiceScope>();
            _serviceProvider = new Mock<IServiceProvider>();
            _apiKeyStore = new Mock<IApiKeyStore>();
            _environment = new Mock<IHostEnvironment>();

            _scopeFactory.Setup(s => s.CreateScope()).Returns(_scope.Object);
            _scope.Setup(s => s.ServiceProvider).Returns(_serviceProvider.Object);
            _serviceProvider.Setup(s => s.GetService(typeof(IApiKeyStore))).Returns(_apiKeyStore.Object);
        }

        private IConfiguration CreateConfig(Dictionary<string, string?> values)
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();
        }

        [Fact]
        public async Task GetPolicyAsync_OptionsRequest_ReturnsPermissiveOriginPolicy()
        {
            // Arrange
            _environment.Setup(e => e.EnvironmentName).Returns("Production");
            var config = CreateConfig(new Dictionary<string, string?>());
            var provider = new DynamicCorsPolicyProvider(_scopeFactory.Object, _environment.Object, config);

            var context = new DefaultHttpContext();
            context.Request.Method = "OPTIONS";
            context.Request.Headers["Origin"] = "https://example.com";

            // Act
            var policy = await provider.GetPolicyAsync(context, "Default");

            // Assert
            policy.Should().NotBeNull();
            policy!.Origins.Should().Contain("https://example.com");
            policy.SupportsCredentials.Should().BeTrue();
        }

        [Fact]
        public async Task GetPolicyAsync_ApiKeyNoOrigins_AllowsAnyOrigin()
        {
            // Arrange
            _environment.Setup(e => e.EnvironmentName).Returns("Production");
            var config = CreateConfig(new Dictionary<string, string?>());
            var provider = new DynamicCorsPolicyProvider(_scopeFactory.Object, _environment.Object, config);

            var rawKey = "azoa_secretkey123";
            var keyHash = AZOA.WebAPI.Services.Auth.ApiKeyAuthenticationHandler.HashKey(rawKey);

            var apiKey = new ApiKey
            {
                KeyHash = keyHash,
                IsActive = true,
                AllowedOrigins = null // Null = allow all
            };

            _apiKeyStore.Setup(s => s.GetByHashAsync(keyHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(apiKey);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Headers["X-Api-Key"] = rawKey;
            context.Request.Headers["Origin"] = "https://dapp.com";

            // Act
            var policy = await provider.GetPolicyAsync(context, "Default");

            // Assert
            policy.Should().NotBeNull();
            policy!.Origins.Should().Contain("https://dapp.com");
            policy.SupportsCredentials.Should().BeTrue();
        }

        [Fact]
        public async Task GetPolicyAsync_ApiKeyWithMatchingOrigins_AllowsOrigin()
        {
            // Arrange
            _environment.Setup(e => e.EnvironmentName).Returns("Production");
            var config = CreateConfig(new Dictionary<string, string?>());
            var provider = new DynamicCorsPolicyProvider(_scopeFactory.Object, _environment.Object, config);

            var rawKey = "azoa_secretkey123";
            var keyHash = AZOA.WebAPI.Services.Auth.ApiKeyAuthenticationHandler.HashKey(rawKey);

            var apiKey = new ApiKey
            {
                KeyHash = keyHash,
                IsActive = true,
                AllowedOrigins = "https://trusted-one.com, https://trusted-two.com"
            };

            _apiKeyStore.Setup(s => s.GetByHashAsync(keyHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(apiKey);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Headers["X-Api-Key"] = rawKey;
            context.Request.Headers["Origin"] = "https://trusted-two.com";

            // Act
            var policy = await provider.GetPolicyAsync(context, "Default");

            // Assert
            policy.Should().NotBeNull();
            policy!.Origins.Should().Contain("https://trusted-two.com");
            policy.SupportsCredentials.Should().BeTrue();
        }

        [Fact]
        public async Task GetPolicyAsync_ApiKeyWithNonMatchingOrigins_BlocksOrigin()
        {
            // Arrange
            _environment.Setup(e => e.EnvironmentName).Returns("Production");
            var config = CreateConfig(new Dictionary<string, string?>());
            var provider = new DynamicCorsPolicyProvider(_scopeFactory.Object, _environment.Object, config);

            var rawKey = "azoa_secretkey123";
            var keyHash = AZOA.WebAPI.Services.Auth.ApiKeyAuthenticationHandler.HashKey(rawKey);

            var apiKey = new ApiKey
            {
                KeyHash = keyHash,
                IsActive = true,
                AllowedOrigins = "https://trusted-one.com"
            };

            _apiKeyStore.Setup(s => s.GetByHashAsync(keyHash, It.IsAny<CancellationToken>()))
                .ReturnsAsync(apiKey);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Headers["X-Api-Key"] = rawKey;
            context.Request.Headers["Origin"] = "https://attacker.com";

            // Act
            var policy = await provider.GetPolicyAsync(context, "Default");

            // Assert
            policy.Should().NotBeNull();
            policy!.Origins.Should().NotContain("https://attacker.com");
        }

        [Fact]
        public async Task GetPolicyAsync_NonApiKeyInDev_AllowsOrigin()
        {
            // Arrange
            _environment.Setup(e => e.EnvironmentName).Returns("Development");
            var config = CreateConfig(new Dictionary<string, string?>());
            var provider = new DynamicCorsPolicyProvider(_scopeFactory.Object, _environment.Object, config);

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Headers["Origin"] = "http://localhost:3000";

            // Act
            var policy = await provider.GetPolicyAsync(context, "Default");

            // Assert
            policy.Should().NotBeNull();
            policy!.Origins.Should().Contain("http://localhost:3000");
            policy.SupportsCredentials.Should().BeTrue();
        }

        [Fact]
        public async Task GetPolicyAsync_NonApiKeyInProdMatchingConfig_AllowsOrigin()
        {
            // Arrange
            _environment.Setup(e => e.EnvironmentName).Returns("Production");
            var config = CreateConfig(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://my-dashboard.com"
            });
            var provider = new DynamicCorsPolicyProvider(_scopeFactory.Object, _environment.Object, config);

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Headers["Origin"] = "https://my-dashboard.com";

            // Act
            var policy = await provider.GetPolicyAsync(context, "Default");

            // Assert
            policy.Should().NotBeNull();
            policy!.Origins.Should().Contain("https://my-dashboard.com");
            policy.SupportsCredentials.Should().BeTrue();
        }

        [Fact]
        public async Task GetPolicyAsync_NonApiKeyInProdNonMatchingConfig_BlocksOrigin()
        {
            // Arrange
            _environment.Setup(e => e.EnvironmentName).Returns("Production");
            var config = CreateConfig(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://my-dashboard.com"
            });
            var provider = new DynamicCorsPolicyProvider(_scopeFactory.Object, _environment.Object, config);

            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Headers["Origin"] = "https://malicious-site.com";

            // Act
            var policy = await provider.GetPolicyAsync(context, "Default");

            // Assert
            policy.Should().NotBeNull();
            policy!.Origins.Should().NotContain("https://malicious-site.com");
        }

        [Fact]
        public async Task GetPolicyAsync_PublicTransparency_IsCredentialFreeAndSkipsApiKeyLookup()
        {
            _environment.Setup(e => e.EnvironmentName).Returns("Production");
            var provider = new DynamicCorsPolicyProvider(
                _scopeFactory.Object,
                _environment.Object,
                CreateConfig([]));
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Headers["Origin"] = "https://any-reader.example";
            context.Request.Headers["X-Api-Key"] = "untrusted-rotating-value";

            var policy = await provider.GetPolicyAsync(
                context,
                DynamicCorsPolicyProvider.PublicTransparencyPolicy);

            policy.Should().NotBeNull();
            policy!.Origins.Should().Equal("*");
            policy.Methods.Should().Equal(HttpMethods.Get);
            policy.SupportsCredentials.Should().BeFalse();
            _scopeFactory.Verify(scopeFactory => scopeFactory.CreateScope(), Times.Never);
        }
    }
}
