using Anthropic.SDK.Constants;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.Services;

public class ModelRouter(
    IChatClient chatClient,
    ILogger<ModelRouter> logger) : IModelRouter
{
    private const string ClassifierPrompt = """
        Classify the complexity of this user request for an AI coding assistant.
        - "simple": quick questions, short explanations, small lookups, greetings
        - "moderate": single-file edits, debugging, code review, explanations of existing code
        - "complex": multi-file refactors, architecture design, complex reasoning, large feature implementation
        Respond with ONLY one word: simple, moderate, or complex.
        """;

    private static readonly Dictionary<ModelMode, string> FixedModels = new()
    {
        [ModelMode.Simple] = AnthropicModels.Claude45Haiku,
        [ModelMode.Moderate] = AnthropicModels.Claude46Sonnet,
        [ModelMode.Complex] = AnthropicModels.Claude46Opus,
    };

    public ModelMode Mode { get; set; } = ModelMode.Auto;

    public async Task<string> ResolveModelAsync(string userMessage, int conversationDepth, CancellationToken cancellationToken = default)
    {
        if (Mode != ModelMode.Auto)
        {
            var model = FixedModels[Mode];
            logger.LogDebug("Model mode {Mode} → {Model}", Mode, model);
            return model;
        }

        // Auto mode: after several turns, lock to Sonnet to keep consistency
        if (conversationDepth > 4)
        {
            logger.LogDebug("Auto mode: deep conversation ({Depth} turns) → Sonnet", conversationDepth);
            return AnthropicModels.Claude46Sonnet;
        }

        return await ClassifyWithHaikuAsync(userMessage, cancellationToken);
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
            var response = await chatClient.GetResponseAsync(
                [
                    new(ChatRole.User, $"{titlePrompt}\n\nUser message: {userMessage}")
                ],
                new ChatOptions { ModelId = AnthropicModels.Claude45Haiku, MaxOutputTokens = 20 },
                cancellationToken);

            var title = response.Text?.Trim().Trim('"', '\'', '.');
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

        // Fallback: first 40 chars of the user message
        return userMessage.Length <= 40 ? userMessage : userMessage[..40] + "…";
    }

    private async Task<string> ClassifyWithHaikuAsync(string userMessage, CancellationToken cancellationToken)
    {
        try
        {
            var response = await chatClient.GetResponseAsync(
                [
                    new(ChatRole.User, $"{ClassifierPrompt}\n\nUser request: {userMessage}")
                ],
                new ChatOptions { ModelId = AnthropicModels.Claude45Haiku, MaxOutputTokens = 10 },
                cancellationToken);

            var classification = response.Text?.Trim().ToLowerInvariant();

            var model = classification switch
            {
                "simple" => AnthropicModels.Claude45Haiku,
                "complex" => AnthropicModels.Claude46Opus,
                _ => AnthropicModels.Claude46Sonnet
            };

            logger.LogInformation("Auto model routing: \"{Classification}\" → {Model}", classification, model);
            return model;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model classification failed, defaulting to Sonnet");
            return AnthropicModels.Claude46Sonnet;
        }
    }
}
