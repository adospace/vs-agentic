using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.ClaudeCli;
using VsAgentic.Services.Configuration;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The usage readings and the model / effort pickers shown in the status bar
/// under the chat input.
///
/// Split out from the main view model because it is a self-contained surface —
/// nothing else in the session depends on it, and keeping it here leaves the
/// conversation logic readable.
/// </summary>
public partial class ChatSessionViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextDisplay))]
    [NotifyPropertyChangedFor(nameof(ContextTooltip))]
    [NotifyPropertyChangedFor(nameof(ContextFraction))]
    [NotifyPropertyChangedFor(nameof(ContextLevel))]
    [NotifyPropertyChangedFor(nameof(SessionTokensDisplay))]
    [NotifyPropertyChangedFor(nameof(SessionTokensTooltip))]
    [NotifyPropertyChangedFor(nameof(ShortWindowDisplay))]
    [NotifyPropertyChangedFor(nameof(ShortWindowTooltip))]
    [NotifyPropertyChangedFor(nameof(LongWindowDisplay))]
    [NotifyPropertyChangedFor(nameof(LongWindowTooltip))]
    [NotifyPropertyChangedFor(nameof(HasWindowMeters))]
    private SessionUsage _usage = SessionUsage.Empty;

    /// <summary>
    /// The model this session runs on, as best known: what the CLI reported
    /// (<see cref="IChatService.CurrentModel"/>) or, before it has, a preview from
    /// <see cref="ClaudeModelResolver"/>. Null when neither can say.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDisplay))]
    [NotifyPropertyChangedFor(nameof(ContextDisplay))]
    [NotifyPropertyChangedFor(nameof(ContextTooltip))]
    [NotifyPropertyChangedFor(nameof(ContextFraction))]
    [NotifyPropertyChangedFor(nameof(ContextLevel))]
    private string? _modelId;

    // ── Model / effort pickers ────────────────────────────────────────────

    /// <summary>
    /// Per-session copies rather than the shared catalog, because the entry for
    /// "whatever the CLI picks" relabels itself to the model that actually
    /// turned up — and two sessions can be on different models.
    /// </summary>
    public IReadOnlyList<ModelOption> ModelOptions { get; } =
        ClaudeModelCatalog.All.Select(m => new ModelOption(m)).ToList();

    public IReadOnlyList<ClaudeEffort> EffortOptions { get; } =
        (ClaudeEffort[])Enum.GetValues(typeof(ClaudeEffort));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDisplay))]
    private ModelOption _selectedModel = null!;

    [ObservableProperty]
    private ClaudeEffort _selectedEffort = ClaudeEffort.Default;

    /// <summary>
    /// Set while seeding the pickers from saved settings, so restoring a value
    /// does not look like the user picking it and needlessly restart the CLI.
    /// </summary>
    private bool _suppressModelEffortApply;

    /// <summary>
    /// Raised when the user picks a model or effort, with the values to store.
    /// The view model only holds them for the life of the session; making the
    /// choice stick across restarts is the host's job, because only the host
    /// knows where its settings live (the VS options page, in the extension's
    /// case). Raised on the UI thread.
    /// </summary>
    public event Action<string, ClaudeEffort>? ModelEffortChanged;

    partial void OnSelectedModelChanged(ModelOption value)
    {
        RefreshResolvedModelLabel();
        if (_suppressModelEffortApply) return;

        ApplyModelAndEffort();
        // A preview taken for the previous choice no longer applies. This also
        // covers the case the service cannot signal: no model reported yet, so
        // clearing it raises nothing.
        RefreshModelPreview();
    }

    partial void OnSelectedEffortChanged(ClaudeEffort value) => ApplyModelAndEffort();

    partial void OnModelIdChanged(string? value) => RefreshResolvedModelLabel();

    private void ApplyModelAndEffort()
    {
        if (_suppressModelEffortApply || _chatService is null) return;

        var alias = SelectedModel?.Alias ?? "";

        try
        {
            _chatService.ApplyModelAndEffort(alias, SelectedEffort);
        }
        catch (Exception ex)
        {
            // A failed switch leaves the session on its current model, which is
            // a working state — not worth tearing the UI down over.
            _logger.LogError(ex, "[VM] Could not apply model/effort change");
            return;
        }

        try { ModelEffortChanged?.Invoke(alias, SelectedEffort); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] ModelEffortChanged handler threw"); }
    }

    /// <summary>
    /// Seeds the pickers from saved settings and starts tracking usage and the
    /// model. Called from the constructor once the chat service is known.
    /// </summary>
    private void InitializeUsage(IChatService chatService, VsAgenticOptions options)
    {
        _suppressModelEffortApply = true;
        try
        {
            var alias = ClaudeModelCatalog.Find(options.Model).Alias;
            SelectedModel = ModelOptions.FirstOrDefault(
                m => string.Equals(m.Alias, alias, StringComparison.OrdinalIgnoreCase))
                ?? ModelOptions[0];
            SelectedEffort = options.Effort;
        }
        finally
        {
            _suppressModelEffortApply = false;
        }

        chatService.UsageChanged += OnChatServiceUsageChanged;
        Usage = chatService.GetUsage();

        chatService.ModelChanged += OnChatServiceModelChanged;
        RefreshModelPreview();
    }

    private void OnChatServiceUsageChanged(SessionUsage usage) => Dispatch(() => Usage = usage);

    private void OnChatServiceModelChanged(string? model) => Dispatch(() =>
    {
        if (model is not null)
        {
            // The real value supersedes any preview still being computed.
            Interlocked.Increment(ref _modelPreviewGeneration);
            ModelId = model;
        }
        else
        {
            // Cleared by a model switch or a new conversation: what was reported
            // no longer applies, so fall back to a preview.
            RefreshModelPreview();
        }
    });

    private int _modelPreviewGeneration;

    /// <summary>
    /// Fills <see cref="ModelId"/> before the CLI has reported it. The CLI emits
    /// <c>system/init</c> with the first message, not at process start, and every
    /// way of forcing it earlier costs an API call — so this reads the same files
    /// the CLI would.
    ///
    /// Which source is right depends on whether the session has run before:
    /// <c>--resume</c> keeps a session on the model it was created with, so the
    /// configured default only answers for a new session. A restored one needs
    /// its transcript.
    ///
    /// Only asked when the user has left the model to the CLI. A picked alias is
    /// passed as <c>--model</c> and the dropdown already names it.
    ///
    /// Runs off the UI thread: it reads settings files and possibly a transcript
    /// many megabytes long.
    /// </summary>
    private void RefreshModelPreview()
    {
        var generation = Interlocked.Increment(ref _modelPreviewGeneration);

        var chatService = _chatService;
        if (chatService is null) return;

        // Start again from what the CLI has reported, dropping any earlier preview.
        ModelId = chatService.CurrentModel;
        if (ModelId is not null) return;
        if (SelectedModel is not { IsCliDefault: true }) return;

        var sessionId = chatService.CliSessionId;
        var workingDirectory = WorkingDirectory;

        Task.Run(() =>
        {
            string? preview;
            try
            {
                preview = ClaudeModelResolver.ResolveSessionModel(sessionId, _logger)
                    ?? ClaudeModelResolver.ResolveConfiguredModel(workingDirectory, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[VM] Model preview failed");
                return;
            }

            if (preview is null) return;

            Dispatch(() =>
            {
                // Do not overwrite a better answer: the CLI's own value, or a
                // later preview that had the session id this one lacked.
                if (Volatile.Read(ref _modelPreviewGeneration) != generation) return;
                if (_chatService?.CurrentModel is not null) return;
                ModelId = preview;
            });
        });
    }

    /// <summary>
    /// Relabels the "let the CLI decide" entry to the model it resolves to, so
    /// the dropdown reads "Opus 5" rather than "Default". Only while that entry
    /// is selected: with an alias picked, the model in force says nothing about
    /// what the CLI would choose on its own.
    /// </summary>
    private void RefreshResolvedModelLabel()
    {
        var resolved = SelectedModel is { IsCliDefault: true } && ModelId is { Length: > 0 }
            ? ClaudeModelCatalog.DisplayNameFor(ModelId)
            : null;

        foreach (var option in ModelOptions)
        {
            if (option.IsCliDefault)
                option.DisplayName = resolved ?? ModelOption.UnresolvedLabel;
        }
    }

    // ── Status bar text ───────────────────────────────────────────────────

    /// <summary>What the model tooltip names: what is running, when that is known.</summary>
    public string ModelDisplay => ModelId is { Length: > 0 }
        ? ClaudeModelCatalog.DisplayNameFor(ModelId)
        : SelectedModel?.DisplayName ?? ClaudeModelCatalog.Default.DisplayName;

    /// <summary>
    /// Follows the model rather than the usage snapshot: the ceiling is a
    /// property of the model id, and only the id the CLI reports carries the
    /// <c>[1m]</c> suffix.
    /// </summary>
    public int ContextWindowTokens => ClaudeModelCatalog.ContextWindowFor(ModelId);

    public double ContextFraction => SessionUsage.Fraction(Usage.ContextTokens, ContextWindowTokens);

    public UsageLevel ContextLevel => SessionUsage.LevelOf(ContextFraction);

    public string ContextDisplay =>
        $"{FormatTokens(Usage.ContextTokens)} / {FormatTokens(ContextWindowTokens)}";

    public string ContextTooltip =>
        "Context window\n"
        + $"Used: {FormatExact(Usage.ContextTokens)} of {FormatExact(ContextWindowTokens)} tokens "
        + $"({ContextFraction:P0})\n"
        + "How full the model's context is right now. This falls back down when the conversation is compacted.";

    public string SessionTokensDisplay => FormatTokens(Usage.TotalTokens);

    public string SessionTokensTooltip =>
        "This session\n"
        + $"Input: {FormatExact(Usage.InputTokens)}\n"
        + $"Output: {FormatExact(Usage.OutputTokens)}\n"
        + $"Cache read: {FormatExact(Usage.CacheReadTokens)}\n"
        + $"Cache write: {FormatExact(Usage.CacheCreationTokens)}\n"
        + $"Total: {FormatExact(Usage.TotalTokens)} tokens"
        + (Usage.CostUsd is { } cost ? $"\nCost: {cost.ToString("C2", CultureInfo.GetCultureInfo("en-US"))}" : "");

    public bool HasWindowMeters => Usage.HasWindowBudget;

    public string ShortWindowDisplay => FormatBudget(Usage.ShortWindowTokens, Usage.ShortWindowBudget);

    public string ShortWindowTooltip => WindowTooltip(
        "Last 5 hours", Usage.ShortWindowTokens, Usage.ShortWindowBudget, Usage.ShortWindowFraction);

    public string LongWindowDisplay => FormatBudget(Usage.LongWindowTokens, Usage.LongWindowBudget);

    public string LongWindowTooltip => WindowTooltip(
        "Last 7 days", Usage.LongWindowTokens, Usage.LongWindowBudget, Usage.LongWindowFraction);

    private static string WindowTooltip(string title, long used, long budget, double fraction) =>
        title + "\n"
        + $"Used: {FormatExact(used)} of about {FormatExact(budget)} tokens ({fraction:P0})\n"
        + "Counted across every VsAgentic session on this machine, and kept between restarts.\n"
        + "The budget is an estimate — Anthropic does not publish the real limit, so treat this "
        + "as a gauge rather than an authority. Adjust it under Tools → Options → VsAgentic.";

    // ── Formatting ────────────────────────────────────────────────────────

    /// <summary>
    /// Compact form for the status bar, where width is scarce: 950, 12.3k, 1.2M.
    /// </summary>
    private static string FormatTokens(long tokens)
    {
        if (tokens <= 0) return "0";
        if (tokens < 1_000) return tokens.ToString(CultureInfo.InvariantCulture);

        if (tokens < 1_000_000)
        {
            var thousands = tokens / 1_000d;
            // Two significant-ish digits under 10k, none above: "9.4k", "127k".
            return thousands < 10
                ? thousands.ToString("0.#", CultureInfo.InvariantCulture) + "k"
                : Math.Round(thousands).ToString("0", CultureInfo.InvariantCulture) + "k";
        }

        var millions = tokens / 1_000_000d;
        return millions < 10
            ? millions.ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : Math.Round(millions).ToString("0", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>Grouped digits for tooltips, where the exact number is the point.</summary>
    private static string FormatExact(long tokens) =>
        tokens.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The tilde marks the budget as the estimate it is. The count beside it is
    /// measured, so it goes without.
    /// </summary>
    private static string FormatBudget(long used, long budget) =>
        budget > 0
            ? $"{FormatTokens(used)} / ~{FormatTokens(budget)}"
            : FormatTokens(used);
}

/// <summary>
/// One row of the model dropdown. A view-model type rather than the plain
/// <see cref="ClaudeModelInfo"/> because the "let the CLI decide" row renames
/// itself once the model it resolves to is known, and that has to raise a
/// change notification for the dropdown to redraw.
/// </summary>
public partial class ModelOption : ObservableObject
{
    /// <summary>
    /// Shown while the model the CLI will choose cannot be read from anywhere:
    /// no transcript, no setting, so the account default applies and only an API
    /// call would name it.
    /// </summary>
    public const string UnresolvedLabel = "Default";

    public ModelOption(ClaudeModelInfo info)
    {
        Alias = info.Alias;
        _displayName = info.IsCliDefault ? UnresolvedLabel : info.DisplayName;
    }

    public string Alias { get; }

    public bool IsCliDefault => Alias.Length == 0;

    [ObservableProperty]
    private string _displayName;

    public override string ToString() => DisplayName;
}
