using System.Text.Json;
using VsAgentic.Services.ClaudeCli;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// The saved state of a fork: no session id of its own, the original's as the
/// one to fork, and the original's model.
/// </summary>
public class ClaudeCliConversationForkerTests
{
    private static readonly ClaudeCliConversationForker Forker = new();

    private static (string? Session, string? ForkFrom, string? Model) Read(string? json)
    {
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        string? Get(string name) =>
            doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return (Get("cliSessionId"), Get(ClaudeCliConversationForker.ForkFromProperty), Get("model"));
    }

    [Fact]
    public void ForksTheConversationAndKeepsTheModel()
    {
        var fork = Read(Forker.CreateForkHistory("{\"cliSessionId\":\"abc\",\"model\":\"claude-opus-5-5[1m]\"}"));

        Assert.Null(fork.Session);
        Assert.Equal("abc", fork.ForkFrom);
        Assert.Equal("claude-opus-5-5[1m]", fork.Model);
    }

    [Fact]
    public void ForkOfAnUnstartedForkForksTheSameConversation()
    {
        var first = Forker.CreateForkHistory("{\"cliSessionId\":\"abc\",\"model\":null}");
        var second = Read(Forker.CreateForkHistory(first));

        Assert.Null(second.Session);
        Assert.Equal("abc", second.ForkFrom);
        Assert.Null(second.Model);
    }

    [Fact]
    public void StartedForkForksItsOwnConversation()
    {
        var fork = Read(Forker.CreateForkHistory("{\"cliSessionId\":\"new\",\"forkFromCliSessionId\":null}"));

        Assert.Equal("new", fork.ForkFrom);
    }

    private static string? Field(string? json, string name)
    {
        using var doc = JsonDocument.Parse(json!);
        return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    [Fact]
    public void TheForkEndsAtTheOriginalsLastMessage()
    {
        var fork = Forker.CreateForkHistory("{\"cliSessionId\":\"abc\",\"lastMessageUuid\":\"m-7\"}");

        Assert.Equal("m-7", Field(fork, ClaudeCliConversationForker.ForkAtProperty));
    }

    [Fact]
    public void AForkOfAnUnstartedForkEndsAtTheSameMessage()
    {
        // The original went on after the first fork (its last message is now
        // m-9); the second fork, of the first, still ends at m-7.
        var first = Forker.CreateForkHistory("{\"cliSessionId\":\"abc\",\"lastMessageUuid\":\"m-7\"}");
        var second = Forker.CreateForkHistory(first);

        Assert.Equal("abc", Field(second, ClaudeCliConversationForker.ForkFromProperty));
        Assert.Equal("m-7", Field(second, ClaudeCliConversationForker.ForkAtProperty));
    }

    [Fact]
    public void WithoutAKnownLastMessageTheForkHasNoEnd()
    {
        // Saved before the field existed: the fork takes the conversation as
        // the CLI holds it at the fork's first message, as before.
        var fork = Forker.CreateForkHistory("{\"cliSessionId\":\"abc\"}");

        Assert.Null(Field(fork, ClaudeCliConversationForker.ForkAtProperty));
    }

    [Fact]
    public void TheUsageReadingsGoWithTheFork()
    {
        var fork = Forker.CreateForkHistory(
            "{\"cliSessionId\":\"abc\",\"usage\":{\"contextTokens\":31000,\"contextWindowTokens\":1000000}}");

        using var doc = JsonDocument.Parse(fork!);
        var usage = doc.RootElement.GetProperty("usage");
        Assert.Equal(31000, usage.GetProperty("contextTokens").GetInt64());
        Assert.Equal(1000000, usage.GetProperty("contextWindowTokens").GetInt64());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"cliSessionId\":null,\"model\":\"opus\"}")]
    [InlineData("{\"cliSessionId\":\"\"}")]
    public void NothingToForkWithoutAConversation(string? history)
    {
        Assert.Null(Forker.CreateForkHistory(history));
    }
}
