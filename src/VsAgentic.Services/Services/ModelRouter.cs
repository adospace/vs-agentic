using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Anthropic;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.Services;

public class ModelRouter(
    AnthropicHttpClient httpClient,
    ILogger<ModelRouter> logger) : IModelRouter
{
    private const string ClassifierPrompt = """
        Classify the complexity of this user request for an AI coding assistant.
        - "simple": quick questions, short explanations, small lookups, greetings
        - "moderate": code review, explanations of existing code, reading files, searching
        - "complex": ANY file editing or creation, debugging, multi-file refactors, architecture design, complex reasoning, feature implementation
        Respond with ONLY one word: simple, moderate, or complex.
        """;

    private static readonly Dictionary<ModelMode, string> FixedModels = new()
    {
        [ModelMode.Simple] = ModelIds.Haiku,
        [ModelMode.Moderate] = ModelIds.Sonnet,
        [ModelMode.Complex] = ModelIds.Opus,
    };

    public ModelMode Mode { get; set; } = ModelMode.Auto;

    private string? _lockedAutoModel;

    public async Task<string> ResolveModelAsync(string userMessage, int conversationDepth, CancellationToken cancellationToken = default)
    {
        // TODO: restore auto-routing once tool content pipeline issues are verified fixed.
        var model = ModelIds.Opus;
        logger.LogDebug("Model routing: forced Opus (debugging mode) — turn {Depth}", conversationDepth);
        return model;
    }

    public void ResetAutoLock()
    {
        _lockedAutoModel = null;
        logger.LogDebug("Auto-mode model lock reset");
    }

    public async Task<string> GenerateTitleAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        const string titlePrompt = """
            Generate a short title (max 6 words) for a coding assistant conversation that starts with the message below.
            The title should capture the intent or topic. Do NOT use quotes or punctuation at the end.
            Respond with ONLY the title, nothing else.
            """;

        try
        {
            var request = new MessagesRequest
            {
                Model = ModelIds.Haiku,
                MaxTokens = 20,
                Messages =
                [
                    new Message { Role = "user", Content = $"{titlePrompt}\n\nUser message: {userMessage}" }
                ],
                Stream = false
            };

            var response = await httpClient.SendAsync(request, cancellationToken);
            var title = AnthropicHttpClient.ExtractText(response)?.Trim().Trim('"', '\'', '.');

            if (!string.IsNullOrWhiteSpace(title))
            {
                logger.LogDebug("Generated session title: \"{Title}\"", title);
                return title!;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Title generation failed");
        }

        return userMessage.Length <= 40 ? userMessage : userMessage[..40] + "…";
    }

    private async Task<string> ClassifyWithHaikuAsync(string userMessage, CancellationToken cancellationToken)
    {
        try
        {
            var request = new MessagesRequest
            {
                Model = ModelIds.Haiku,
                MaxTokens = 10,
                Messages =
                [
                    new Message { Role = "user", Content = $"{ClassifierPrompt}\n\nUser request: {userMessage}" }
                ],
                Stream = false
            };

            var response = await httpClient.SendAsync(request, cancellationToken);
            var classification = AnthropicHttpClient.ExtractText(response)?.Trim().ToLowerInvariant();

            var model = classification switch
            {
                "simple" => ModelIds.Haiku,
                "complex" => ModelIds.Opus,
                _ => ModelIds.Sonnet
            };

            logger.LogInformation("Auto model routing: \"{Classification}\" → {Model}", classification, model);
            return model;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model classification failed, defaulting to Sonnet");
            return ModelIds.Sonnet;
        }
    }
}
