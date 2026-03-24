using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.Anthropic;

/// <summary>
/// Thin HTTP client for the Anthropic Messages API.
/// Handles streaming SSE and non-streaming requests.
/// </summary>
public sealed class AnthropicHttpClient
{
    private const string BaseUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // No naming policy — our types use explicit [JsonPropertyName] attributes with snake_case
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public AnthropicHttpClient(string apiKey, ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        // Required for prompt caching to activate
        _httpClient.DefaultRequestHeaders.Add("anthropic-beta", "prompt-caching-2024-07-31");
    }

    /// <summary>
    /// Sends a streaming request. Returns the raw response stream for SSE parsing.
    /// The caller is responsible for parsing SSE events via SseParser.
    /// </summary>
    public async Task<Stream> StreamAsync(MessagesRequest request, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(request, SerializerOptions);
        _logger.LogTrace("[API] Streaming request ({Len} chars):\n{Json}", json.Length, json);

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl)
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("[API] Error {StatusCode}: {Body}", response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        return await response.Content.ReadAsStreamAsync();
    }

    /// <summary>
    /// Sends a non-streaming request and returns the full response.
    /// Used for simple operations like title generation and model classification.
    /// </summary>
    public async Task<MessagesResponse> SendAsync(MessagesRequest request, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(request, SerializerOptions);
        _logger.LogTrace("[API] Non-streaming request ({Len} chars)", json.Length);

        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(BaseUrl, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("[API] Error {StatusCode}: {Body}", response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        var responseJson = await response.Content.ReadAsStringAsync();
        _logger.LogTrace("[API] Response ({Len} chars)", responseJson.Length);

        return JsonSerializer.Deserialize<MessagesResponse>(responseJson, SerializerOptions)
               ?? throw new InvalidOperationException("Failed to deserialize API response");
    }

    /// <summary>
    /// Extracts the text content from a non-streaming response.
    /// </summary>
    public static string? ExtractText(MessagesResponse response)
    {
        foreach (var block in response.Content)
        {
            if (block.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text")
            {
                if (block.TryGetProperty("text", out var textProp))
                    return textProp.GetString();
            }
        }
        return null;
    }
}
