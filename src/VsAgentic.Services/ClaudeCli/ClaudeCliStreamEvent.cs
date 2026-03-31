using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Represents a single line of newline-delimited JSON from
/// <c>claude -p --output-format stream-json --verbose</c>.
///
/// Known event shapes:
///   {"type":"system",  "subtype":"init", "session_id":"...", "tools":[...], "model":"..."}
///   {"type":"assistant","message":{...},  "session_id":"..."}
///   {"type":"user",    "message":{...},  "session_id":"..."}
///   {"type":"result",  "subtype":"success|error", "result":"...", "session_id":"...",
///        "cost_usd":0.005, "is_error":false, "duration_ms":3500, "num_turns":1}
/// </summary>
public sealed class ClaudeCliStreamEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; set; }

    /// <summary>
    /// Present on "assistant" and "user" events.
    /// Contains the full message object with role, content blocks, usage, etc.
    /// </summary>
    [JsonPropertyName("message")]
    public JsonElement? Message { get; set; }

    // ── Result fields ─────────────────────────────────────────────────────

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("cost_usd")]
    public decimal? CostUsd { get; set; }

    [JsonPropertyName("is_error")]
    public bool? IsError { get; set; }

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; set; }

    [JsonPropertyName("num_turns")]
    public int? NumTurns { get; set; }
}
