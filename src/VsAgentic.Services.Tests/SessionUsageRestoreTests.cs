using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;
using VsAgentic.Services.Configuration;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// A reopened session shows the readings it had when it was saved, not
/// "Session 0" and "0 / 200k" until its next reply.
/// </summary>
public class SessionUsageRestoreTests
{
    private static ClaudeCliChatService NewService()
    {
        var options = Options.Create(new VsAgenticOptions());
        var host = new ClaudeCliProcessHost(
            options,
            new PermissionBroker(NullLogger<PermissionBroker>.Instance),
            new UserQuestionBroker(NullLogger<UserQuestionBroker>.Instance),
            NullLogger<ClaudeCliProcessHost>.Instance);
        return new ClaudeCliChatService(options, new NullListener(), host, NullLogger<ClaudeCliChatService>.Instance);
    }

    private const string Saved =
        "{\"cliSessionId\":\"abc\",\"model\":\"claude-opus-5-5\",\"usage\":{"
        + "\"inputTokens\":1200,\"outputTokens\":34000,\"cacheReadTokens\":700000,\"cacheCreationTokens\":49000,"
        + "\"contextTokens\":40123,\"contextWindowTokens\":1000000,\"costUsd\":3.49}}";

    [Fact]
    public void Restore_brings_back_the_readings()
    {
        using var service = NewService();
        SessionUsage? raised = null;
        service.UsageChanged += u => raised = u;

        service.RestoreHistory(Saved);

        var usage = service.GetUsage();
        Assert.Equal(1200, usage.InputTokens);
        Assert.Equal(34000, usage.OutputTokens);
        Assert.Equal(700000, usage.CacheReadTokens);
        Assert.Equal(49000, usage.CacheCreationTokens);
        Assert.Equal(784200, usage.TotalTokens);
        Assert.Equal(40123, usage.ContextTokens);
        // Restored after the model, which clears the window when it is set.
        Assert.Equal(1000000L, usage.ContextWindowTokens);
        Assert.Equal(3.49m, usage.CostUsd);
        Assert.Equal("claude-opus-5-5", service.CurrentModel);
        Assert.NotNull(raised);
        Assert.Equal(40123, raised!.ContextTokens);
    }

    [Fact]
    public void Saved_readings_survive_a_second_round()
    {
        using var first = NewService();
        first.RestoreHistory(Saved);

        using var second = NewService();
        second.RestoreHistory(first.SerializeHistory());

        Assert.Equal(first.GetUsage().TotalTokens, second.GetUsage().TotalTokens);
        Assert.Equal(40123, second.GetUsage().ContextTokens);
        Assert.Equal(1000000L, second.GetUsage().ContextWindowTokens);
        Assert.Equal(3.49m, second.GetUsage().CostUsd);
        Assert.Equal("abc", second.CliSessionId);
    }

    [Fact]
    public void Session_saved_by_an_older_build_reads_zero()
    {
        using var service = NewService();
        service.RestoreHistory("{\"cliSessionId\":\"abc\",\"model\":\"claude-opus-5-5\"}");

        var usage = service.GetUsage();
        Assert.Equal(0, usage.TotalTokens);
        Assert.Equal(0, usage.ContextTokens);
        Assert.Null(usage.ContextWindowTokens);
        Assert.Null(usage.CostUsd);
        Assert.Equal("abc", service.CliSessionId);
    }

    [Fact]
    public void A_new_session_saves_no_window_and_no_cost()
    {
        using var service = NewService();
        using var doc = JsonDocument.Parse(service.SerializeHistory());
        var usage = doc.RootElement.GetProperty("usage");

        Assert.Equal(JsonValueKind.Null, usage.GetProperty("contextWindowTokens").ValueKind);
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("costUsd").ValueKind);
        Assert.Equal(0, usage.GetProperty("contextTokens").GetInt64());
    }

    private sealed class NullListener : IOutputListener
    {
        public void OnStepStarted(OutputItem item) { }
        public void OnStepUpdated(OutputItem item) { }
        public void OnStepCompleted(OutputItem item) { }
    }
}
