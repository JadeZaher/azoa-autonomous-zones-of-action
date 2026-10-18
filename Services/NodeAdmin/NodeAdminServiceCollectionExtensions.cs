using AZOA.WebAPI.Managers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AZOA.WebAPI.Services.NodeAdmin;

/// <summary>Registers the node-admin identity, authorization and audit surface.</summary>
public static class NodeAdminServiceCollectionExtensions
{
    public static IServiceCollection AddNodeAdmin(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<NodeAdminOptions>().Bind(configuration.GetSection(NodeAdminOptions.SectionName));
        services.AddSingleton<NodeAdminRuntime>();
        services.AddSingleton<NodeAdminPermissionResolver>();
        services.AddSingleton<NodeAdminTokenIssuer>();
        services.AddSingleton<NodeAdminTotpProtector>();
        services.AddScoped<SurrealNodeAdminStore>();
        services.AddScoped<NodeAdminAuditLog>();
        services.AddScoped<NodeAdminSessionValidator>();
        services.AddScoped<NodeAdminAuthManager>();
        services.AddScoped<NodeAdminManager>();
        services.AddScoped<NodeAdminAuditActionFilter>();
        services.AddSingleton<IAuthorizationHandler, NodeAdminPermissionHandler>();
        services.AddSingleton<IAuthorizationPolicyProvider, NodeAdminAuthorizationPolicyProvider>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<NodeAdminAuditActionFilter>());
        services.AddHostedService<NodeAdminBootstrapService>();
        services.AddHostedService<NodeAdminSweepWorker>();
        return services;
    }
}
