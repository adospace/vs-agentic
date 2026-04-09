using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
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

        // Brokers — both singletons; UI subscribes to events on construction.
        services.AddSingleton<IPermissionBroker, PermissionBroker>();
        services.AddSingleton<IUserQuestionBroker, UserQuestionBroker>();

        // Long-running CLI process host — singleton, owns the subprocess and
        // the in-process pipe server that the MCP helper exe connects to.
        services.AddSingleton<ClaudeCliProcessHost>();

        services.AddSingleton<IChatService>(sp =>
            new ClaudeCliChatService(
                sp.GetRequiredService<IOptions<VsAgenticOptions>>(),
                sp.GetRequiredService<IOutputListener>(),
                sp.GetRequiredService<IUserQuestionBroker>(),
                sp.GetRequiredService<ClaudeCliProcessHost>(),
                sp.GetRequiredService<ILogger<ClaudeCliChatService>>()));

        return services;
    }
}
