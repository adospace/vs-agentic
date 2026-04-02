using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using VsAgentic.Services.Configuration;
using VsAgentic.Services.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VsAgentic.Services.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVsAgenticServices(
        this IServiceCollection services,
        Action<VsAgenticOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<VsAgenticOptions>(_ => { });

        services.AddSingleton<ISessionStore, JsonSessionStore>();

        services.AddSingleton<IChatService>(sp =>
            new ClaudeCliChatService(
                sp.GetRequiredService<IOptions<VsAgenticOptions>>(),
                sp.GetRequiredService<IOutputListener>(),
                sp.GetRequiredService<ILogger<ClaudeCliChatService>>()));

        return services;
    }
}
