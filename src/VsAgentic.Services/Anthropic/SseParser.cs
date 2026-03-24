using System.Runtime.CompilerServices;
using System.Text.Json;

namespace VsAgentic.Services.Anthropic;

/// <summary>
/// Parses a Server-Sent Events (SSE) stream into structured events.
/// Each event has an event type (from "event:" lines) and a JSON data payload (from "data:" lines).
/// </summary>
public static class SseParser
{
    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream);

        string? eventType = null;
        string? dataLine = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break; // end of stream

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventType = line.Substring(6).Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLine = line.Substring(5).TrimStart();
            }
            else if (line.Length == 0)
            {
                // Empty line = event boundary
                if (eventType != null && dataLine != null)
                {
                    // Skip [DONE] sentinel
                    if (dataLine != "[DONE]")
                    {
                        JsonElement data;
                        try
                        {
                            using var doc = JsonDocument.Parse(dataLine);
                            data = doc.RootElement.Clone();
                        }
                        catch (JsonException)
                        {
                            // Skip malformed events
                            eventType = null;
                            dataLine = null;
                            continue;
                        }

                        yield return new SseEvent
                        {
                            EventType = eventType,
                            Data = data
                        };
                    }

                    eventType = null;
                    dataLine = null;
                }
            }
        }
    }
}
