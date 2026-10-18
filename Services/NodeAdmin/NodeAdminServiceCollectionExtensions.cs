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
        services.AddSingleton<NodeAdminAnonymousFailures>();
        services.AddSingleton<NodeAdminChallengeCodec>();
        services.AddScoped<SurrealNodeAdminStore>();
        services.AddScoped<NodeAdminAuditLog>();
        services.AddScoped<NodeAdminSessionValidator>();
        services.AddScoped<NodeAdminAuthManager>();
        services.AddScoped<NodeAdminManager>();
        services.AddScoped<NodeAdminAuditActionFilter>();
        services.AddSingleton<IAuthorizationHandler, NodeAdminPermissionHandler>();
        services.AddSingleton<IAuthorizationPolicyProvider, NodeAdminAuthorizationPolicyProvider>();
        // Order before ModelStateInvalidFilter (-2000) so automatic 400s on node-admin writes are audited too (L-3).
        services.Configure<MvcOptions>(options => options.Filters.AddService<NodeAdminAuditActionFilter>(NodeAdminAuditActionFilter.FilterOrder));
        services.AddHostedService<NodeAdminBootstrapService>();
        services.AddHostedService<NodeAdminSweepWorker>();
        services.AddHostedService<NodeAdminAnonymousFailureFlusher>();
        return services;
    }
}
