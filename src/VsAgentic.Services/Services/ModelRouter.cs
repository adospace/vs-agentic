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
        - "moderate": most coding tasks — editing files, bug fixes, adding features, refactoring within a few files, debugging, code review, reading/searching code
        - "complex": ONLY tasks requiring deep architectural reasoning across many files, large-scale redesigns, or tasks explicitly requesting thorough analysis of an entire system
        Default to "moderate" when uncertain. Most coding requests are moderate.
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

    private static readonly string[] ModelTier = [ModelIds.Haiku, ModelIds.Sonnet, ModelIds.Opus];

    public async Task<string> ResolveModelAsync(string userMessage, int conversationDepth, CancellationToken cancellationToken = default)
    {
        // Fixed mode — always use the configured model
        if (Mode != ModelMode.Auto)
        {
            var fixedModel = FixedModels[Mode];
            logger.LogDebug("Model routing: fixed {Mode} → {Model}", Mode, fixedModel);
            return fixedModel;
        }

        // Already locked at highest tier — skip the classification call entirely
        if (_lockedAutoModel is not null && GetTierIndex(_lockedAutoModel) >= ModelTier.Length - 1)
        {
            logger.LogDebug("Model routing: already at max tier {Model}, skipping classification", _lockedAutoModel);
            return _lockedAutoModel;
        }

        // Auto mode — classify with Haiku, but only allow upward escalation
        var classified = await ClassifyWithHaikuAsync(userMessage, cancellationToken);

        if (_lockedAutoModel is null)
        {
            // First turn: lock to whatever was classified
            _lockedAutoModel = classified;
            logger.LogDebug("Model routing: auto initial → {Model} (turn {Depth})", classified, conversationDepth);
            return classified;
        }

        // Subsequent turns: only escalate upward, never downgrade
        if (GetTierIndex(classified) > GetTierIndex(_lockedAutoModel))
        {
            logger.LogInformation("Model routing: escalating {Old} → {New} (turn {Depth})", _lockedAutoModel, classified, conversationDepth);
            _lockedAutoModel = classified;
        }
        else
        {
            logger.LogDebug("Model routing: staying at {Model} (classified {Classified}, turn {Depth})", _lockedAutoModel, classified, conversationDepth);
        }

        return _lockedAutoModel;
    }

    private static int GetTierIndex(string modelId)
    {
        var index = Array.IndexOf(ModelTier, modelId);
        return index >= 0 ? index : 1; // default to middle tier if unknown
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
