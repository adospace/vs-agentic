using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace VsAgentic.Services.ClaudeCli.Questions;

public sealed class UserQuestionBroker : IUserQuestionBroker
{
    private readonly ILogger<UserQuestionBroker> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyDictionary<string, string>>> _pending = new();

    public UserQuestionBroker(ILogger<UserQuestionBroker> logger)
    {
        _logger = logger;
    }

    public event Action<UserQuestionRequest>? QuestionRequested;

    public Task<IReadOnlyDictionary<string, string>> SubmitAsync(
        UserQuestionRequest request,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<IReadOnlyDictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(request.ToolUseId, tcs))
        {
            _logger.LogWarning("[UserQuestionBroker] Duplicate tool_use id {Id}", request.ToolUseId);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(request.ToolUseId, out var pending))
                pending.TrySetResult(new Dictionary<string, string>());
        });
        tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);

        try
        {
            QuestionRequested?.Invoke(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UserQuestionBroker] QuestionRequested handler threw");
        }

        return tcs.Task;
    }

    public void Resolve(string toolUseId, IReadOnlyDictionary<string, string> answers)
    {
        if (_pending.TryRemove(toolUseId, out var tcs))
        {
            tcs.TrySetResult(answers);
        }
        else
        {
            _logger.LogWarning("[UserQuestionBroker] Resolve for unknown tool_use id {Id}", toolUseId);
        }
    }
}
