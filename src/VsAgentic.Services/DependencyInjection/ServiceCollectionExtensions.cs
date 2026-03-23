using System.Net;
using System.Net.Http;
using Anthropic.SDK;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using VsAgentic.Services.Services;
using VsAgentic.Services.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

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
        services.AddSingleton<IFileSessionTracker, FileSessionTracker>();
        services.AddSingleton<IBashToolService, BashToolService>();
        services.AddSingleton<IGropToolService, GropToolService>();
        services.AddSingleton<IGrebToolService, GrebToolService>();
        services.AddSingleton<IReadToolService, ReadToolService>();
        services.AddSingleton<IEditToolService, EditToolService>();
        services.AddSingleton<IWriteToolService, WriteToolService>();
        services.AddSingleton<IAgentToolService, AgentToolService>();
        services.AddSingleton<IWebFetchToolService, WebFetchToolService>();

        // Base tools (keyed) — available to the Agent sub-session
        services.AddKeyedSingleton<AITool>("base", (sp, _) =>
            (AITool)BashTool.Create(sp.GetRequiredService<IBashToolService>()));
        services.AddKeyedSingleton<AITool>("base", (sp, _) =>
            (AITool)GropTool.Create(sp.GetRequiredService<IGropToolService>()));
        services.AddKeyedSingleton<AITool>("base", (sp, _) =>
            (AITool)GrebTool.Create(sp.GetRequiredService<IGrebToolService>()));
        services.AddKeyedSingleton<AITool>("base", (sp, _) =>
            (AITool)ReadTool.Create(sp.GetRequiredService<IReadToolService>()));
        services.AddKeyedSingleton<AITool>("base", (sp, _) =>
            (AITool)EditTool.Create(sp.GetRequiredService<IEditToolService>()));
        services.AddKeyedSingleton<AITool>("base", (sp, _) =>
            (AITool)WriteTool.Create(sp.GetRequiredService<IWriteToolService>()));
        services.AddKeyedSingleton<AITool>("base", (sp, _) =>
            (AITool)WebFetchTool.Create(sp.GetRequiredService<IWebFetchToolService>()));

        // All tools (unkeyed) — available to the main ChatService
        services.AddSingleton<AITool>(sp =>
            BashTool.Create(sp.GetRequiredService<IBashToolService>()));
        services.AddSingleton<AITool>(sp =>
            GropTool.Create(sp.GetRequiredService<IGropToolService>()));
        services.AddSingleton<AITool>(sp =>
            GrebTool.Create(sp.GetRequiredService<IGrebToolService>()));
        services.AddSingleton<AITool>(sp =>
            ReadTool.Create(sp.GetRequiredService<IReadToolService>()));
        services.AddSingleton<AITool>(sp =>
            EditTool.Create(sp.GetRequiredService<IEditToolService>()));
        services.AddSingleton<AITool>(sp =>
            WriteTool.Create(sp.GetRequiredService<IWriteToolService>()));
        services.AddSingleton<AITool>(sp =>
            AgentTool.Create(sp.GetRequiredService<IAgentToolService>()));
        services.AddSingleton<AITool>(sp =>
            WebFetchTool.Create(sp.GetRequiredService<IWebFetchToolService>()));

        services.AddChatClient(sp =>
        {
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException(
                    "ANTHROPIC_API_KEY environment variable is required. Set it before running the application.");

            // AnthropicClient reads ANTHROPIC_API_KEY from env var automatically
            // .Messages implements IChatClient directly
            return new AnthropicClient().Messages;
        })
        .UseFunctionInvocation();

        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ResiliencePipeline>>();
            return new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = TimeSpan.FromSeconds(2),
                    ShouldHandle = new PredicateBuilder()
                        .Handle<HttpRequestException>(ex =>
                            ex.Message.Contains("429")
                            || ex.Message.Contains("503")
                            || ex.Message.Contains("502")
                            || ex.Message.Contains("504")
                            || ex.Message.Contains("529"))
                        .Handle<Exception>(ex =>
                            ex.Message.Contains("overload", StringComparison.OrdinalIgnoreCase)),
                    OnRetry = args =>
                    {
                        logger.LogWarning(args.Outcome.Exception,
                            "Anthropic API call failed (attempt {AttemptNumber}), retrying in {RetryDelay}s: {Message}",
                            args.AttemptNumber + 1,
                            args.RetryDelay.TotalSeconds,
                            args.Outcome.Exception?.Message);
                        return new ValueTask(Task.CompletedTask);
                    }
                })
                .Build();
        });

        services.AddSingleton<IModelRouter, ModelRouter>();
        services.AddSingleton<IChatService, ChatService>();

        return services;
    }
}
