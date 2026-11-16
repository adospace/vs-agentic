using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
using VsAgentic.Services.Configuration;
using VsAgentic.Services.IdeContext;
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

        // The IDE tools of the vsagentic MCP server. The host registers its
        // IIdeContextProvider instances; without any, the server has only the
        // permission tool, as before.
        services.AddSingleton<IdeContextRegistry>();

        // Long-running CLI process host — singleton, owns the subprocess and
        // the in-process pipe server that the MCP helper exe connects to.
        services.AddSingleton<ClaudeCliProcessHost>();

        services.AddSingleton<IChatService>(sp =>
            new ClaudeCliChatService(
                sp.GetRequiredService<IOptions<VsAgenticOptions>>(),
                sp.GetRequiredService<IOutputListener>(),
                sp.GetRequiredService<ClaudeCliProcessHost>(),
                sp.GetRequiredService<ILogger<ClaudeCliChatService>>())
            {
                IdeContext = sp.GetRequiredService<IdeContextRegistry>(),
            });

        return services;
    }
}
