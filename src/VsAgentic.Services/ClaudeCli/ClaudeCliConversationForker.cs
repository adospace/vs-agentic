using System.Text.Json;
using System.Text.Json.Nodes;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.Services.ClaudeCli;

/// <summary>
/// Forks a Claude Code conversation. The new chat keeps no session id of its
/// own, only the original's under <see cref="ForkFromProperty"/> and the
/// message to end at under <see cref="ForkAtProperty"/>; its first start runs
/// <c>claude --resume &lt;id&gt; --fork-session --resume-session-at &lt;message&gt;</c>,
/// and the CLI then copies the conversation up to that message under a new
/// id. What the original says after the fork stays out of the copy, however
/// late the fork's first message comes. Nothing is copied before the user
/// sends a message, so a fork that is never used leaves nothing in the CLI's
/// own store.
/// </summary>
public sealed class ClaudeCliConversationForker : IConversationForker
{
    /// <summary>The field of the history JSON that names the conversation to fork.</summary>
    public const string ForkFromProperty = "forkFromCliSessionId";

    /// <summary>The field that names the message the fork ends at.</summary>
    public const string ForkAtProperty = "forkAtMessageUuid";

    /// <summary>The field where a chat keeps its conversation's last message.</summary>
    public const string LastMessageProperty = "lastMessageUuid";

    public string? CreateForkHistory(string? serializedHistory)
    {
        if (string.IsNullOrEmpty(serializedHistory)) return null;

        try
        {
            using var doc = JsonDocument.Parse(serializedHistory!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // A chat with a conversation of its own forks at its last message.
            // A fork of a fork that has not started yet forks the same
            // conversation at the same message its source would have.
            string? source, at;
            if (ReadString(root, "cliSessionId") is { } own)
            {
                source = own;
                at = ReadString(root, LastMessageProperty);
            }
            else
            {
                source = ReadString(root, ForkFromProperty);
                at = ReadString(root, ForkAtProperty);
            }
            if (source is null) return null;

            // The model goes with it: --resume keeps the conversation on the
            // model it ran on, and the panel shows it from here. So do the
            // usage readings: the context meter would read 0 until the fork's
            // first reply, though the fork holds the same conversation.
            var fork = new JsonObject
            {
                ["cliSessionId"] = null,
                ["model"] = ReadString(root, "model"),
                [ForkFromProperty] = source,
                [ForkAtProperty] = at,
            };
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                fork["usage"] = JsonNode.Parse(usage.GetRawText());

            return fork.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(value.GetString())
            ? value.GetString()
            : null;
}
