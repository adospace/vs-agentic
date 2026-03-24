using System.Net.Http;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Configuration;
using VsAgentic.Services.Services;
using VsAgentic.Services.Tools;
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

        // ── Anthropic HTTP client ──────────────────────────────────────────────
        services.AddSingleton(sp =>
        {
            var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException(
                    "ANTHROPIC_API_KEY environment variable is required. Set it before running the application.");

            var logger = sp.GetRequiredService<ILogger<AnthropicHttpClient>>();
            return new AnthropicHttpClient(apiKey, logger);
        });

        // ── Base tools (keyed) — available to the Agent sub-session ────────────
        services.AddKeyedSingleton<ToolDefinition>("base", (sp, _) =>
            BashTool.Create(sp.GetRequiredService<IBashToolService>()));
        services.AddKeyedSingleton<ToolDefinition>("base", (sp, _) =>
            GropTool.Create(sp.GetRequiredService<IGropToolService>()));
        services.AddKeyedSingleton<ToolDefinition>("base", (sp, _) =>
            GrebTool.Create(sp.GetRequiredService<IGrebToolService>()));
        services.AddKeyedSingleton<ToolDefinition>("base", (sp, _) =>
            ReadTool.Create(sp.GetRequiredService<IReadToolService>()));
        services.AddKeyedSingleton<ToolDefinition>("base", (sp, _) =>
            EditTool.Create(sp.GetRequiredService<IEditToolService>()));
        services.AddKeyedSingleton<ToolDefinition>("base", (sp, _) =>
            WriteTool.Create(sp.GetRequiredService<IWriteToolService>()));
        services.AddKeyedSingleton<ToolDefinition>("base", (sp, _) =>
            WebFetchTool.Create(sp.GetRequiredService<IWebFetchToolService>()));

        // ── All tools (unkeyed) — available to the main ChatService ────────────
        services.AddSingleton(sp =>
            BashTool.Create(sp.GetRequiredService<IBashToolService>()));
        services.AddSingleton(sp =>
            GropTool.Create(sp.GetRequiredService<IGropToolService>()));
        services.AddSingleton(sp =>
            GrebTool.Create(sp.GetRequiredService<IGrebToolService>()));
        services.AddSingleton(sp =>
            ReadTool.Create(sp.GetRequiredService<IReadToolService>()));
        services.AddSingleton(sp =>
            EditTool.Create(sp.GetRequiredService<IEditToolService>()));
        services.AddSingleton(sp =>
            WriteTool.Create(sp.GetRequiredService<IWriteToolService>()));
        services.AddSingleton(sp =>
            AgentTool.Create(sp.GetRequiredService<IAgentToolService>()));
        services.AddSingleton(sp =>
            WebFetchTool.Create(sp.GetRequiredService<IWebFetchToolService>()));

        // ── Resilience pipeline (Polly retry for rate limits) ──────────────────
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
