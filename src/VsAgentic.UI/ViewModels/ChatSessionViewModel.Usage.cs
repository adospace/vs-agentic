using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using VsAgentic.Services.Abstractions;
using VsAgentic.Services.Configuration;

namespace VsAgentic.UI.ViewModels;

/// <summary>
/// The usage meters and the model / effort pickers shown in the chat header.
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
    [NotifyPropertyChangedFor(nameof(SessionTokensDisplay))]
    [NotifyPropertyChangedFor(nameof(SessionTokensTooltip))]
    [NotifyPropertyChangedFor(nameof(ShortWindowDisplay))]
    [NotifyPropertyChangedFor(nameof(ShortWindowTooltip))]
    [NotifyPropertyChangedFor(nameof(LongWindowDisplay))]
    [NotifyPropertyChangedFor(nameof(LongWindowTooltip))]
    [NotifyPropertyChangedFor(nameof(HasWindowMeters))]
    [NotifyPropertyChangedFor(nameof(ModelDisplay))]
    private SessionUsage _usage = SessionUsage.Empty;

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
    private ClaudeEffort _selectedEffort = ClaudeEffort.High;

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

    partial void OnSelectedModelChanged(ModelOption value) => ApplyModelAndEffort();

    partial void OnSelectedEffortChanged(ClaudeEffort value) => ApplyModelAndEffort();

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
    /// Seeds the pickers from saved settings and starts tracking usage.
    /// Called from the constructor once the chat service is known.
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
        RefreshResolvedModelLabel();
    }

    private void OnChatServiceUsageChanged(SessionUsage usage) => Dispatch(() =>
    {
        Usage = usage;
        RefreshResolvedModelLabel();
    });

    /// <summary>
    /// Relabels the "let the CLI decide" entry to the model that actually
    /// turned up, so the dropdown reads "Opus 5" rather than "Default". The
    /// other entries are fixed aliases and already say what they mean.
    /// </summary>
    private void RefreshResolvedModelLabel()
    {
        var resolved = Usage.ModelId is { Length: > 0 }
            ? ClaudeModelCatalog.DisplayNameFor(Usage.ModelId)
            : null;

        foreach (var option in ModelOptions)
        {
            if (option.IsCliDefault)
                option.DisplayName = resolved ?? ModelOption.PendingLabel;
        }
    }

    // ── Header text ───────────────────────────────────────────────────────

    /// <summary>What the model chip shows: what is actually running, when the CLI has said.</summary>
    public string ModelDisplay => Usage.ModelId is { Length: > 0 }
        ? ClaudeModelCatalog.DisplayNameFor(Usage.ModelId)
        : SelectedModel?.DisplayName ?? ClaudeModelCatalog.Default.DisplayName;

    public string ContextDisplay =>
        $"{FormatTokens(Usage.ContextTokens)} / {FormatTokens(Usage.ContextWindowTokens)}";

    public string ContextTooltip =>
        "Context window\n"
        + $"Used: {FormatExact(Usage.ContextTokens)} of {FormatExact(Usage.ContextWindowTokens)} tokens "
        + $"({Usage.ContextFraction:P0})\n"
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

    // The window meters carry their own prefix because the bars no longer have
    // captions above them — inside the bar is the only place left to say which
    // window this is.
    public string ShortWindowDisplay => "5h " + FormatBudget(Usage.ShortWindowTokens, Usage.ShortWindowBudget);

    public string ShortWindowTooltip => WindowTooltip(
        "Last 5 hours", Usage.ShortWindowTokens, Usage.ShortWindowBudget, Usage.ShortWindowFraction);

    public string LongWindowDisplay => "7d " + FormatBudget(Usage.LongWindowTokens, Usage.LongWindowBudget);

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
    /// Compact form for the header, where width is scarce: 950, 12.3k, 1.2M.
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

    private static string FormatBudget(long used, long budget) =>
        budget > 0
            ? $"{FormatTokens(used)} / {FormatTokens(budget)}"
            : FormatTokens(used);
}

/// <summary>
/// One row of the model dropdown. A view-model type rather than the plain
/// <see cref="ClaudeModelInfo"/> because the "let the CLI decide" row renames
/// itself once the CLI says what it picked, and that has to raise a change
/// notification for the dropdown to redraw.
/// </summary>
public partial class ModelOption : ObservableObject
{
    /// <summary>
    /// Shown until the first turn reveals the real model. "Auto" rather than
    /// "Default" because it describes who is choosing without naming a value we
    /// do not yet know.
    /// </summary>
    public const string PendingLabel = "Auto";

    public ModelOption(ClaudeModelInfo info)
    {
        Alias = info.Alias;
        _displayName = info.IsCliDefault ? PendingLabel : info.DisplayName;
    }

    public string Alias { get; }

    public bool IsCliDefault => Alias.Length == 0;

    [ObservableProperty]
    private string _displayName;

    public override string ToString() => DisplayName;
}
