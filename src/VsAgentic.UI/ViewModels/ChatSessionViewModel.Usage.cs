using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    [NotifyPropertyChangedFor(nameof(ShortWindowLevel))]
    [NotifyPropertyChangedFor(nameof(LongWindowDisplay))]
    [NotifyPropertyChangedFor(nameof(LongWindowTooltip))]
    [NotifyPropertyChangedFor(nameof(LongWindowLevel))]
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
    ///
    /// The rows are the models the CLI offered (<see cref="ClaudeModelCatalog.Reported"/>),
    /// or the built-in aliases until a CLI has answered.
    /// </summary>
    public IReadOnlyList<ModelOption> ModelOptions => _modelOptions;

    private List<ModelOption> _modelOptions = BuildModelOptions(null);

    // The reported list the rows were built from; null for the built-in one.
    private IReadOnlyList<ClaudeModelInfo>? _modelOptionsSource;

    private static List<ModelOption> BuildModelOptions(IReadOnlyList<ClaudeModelInfo>? reported)
    {
        var rows = (reported ?? ClaudeModelCatalog.All).Select(m => new ModelOption(m)).ToList();

        // Default goes first, and must exist: a configured empty alias selects it.
        var cliDefault = rows.FirstOrDefault(r => r.IsCliDefault) ?? new ModelOption(ClaudeModelCatalog.Default);
        rows.Remove(cliDefault);
        rows.Insert(0, cliDefault);
        return rows;
    }

    /// <summary>
    /// The levels the selected model accepts, after Default. The selected
    /// level stays in the list even when the model does not accept it, so
    /// switching models does not quietly change the user's choice; the CLI
    /// lowers it, and <see cref="EffortTooltip"/> says to what.
    /// </summary>
    public IReadOnlyList<EffortOption> EffortOptions => _effortOptions;

    private IReadOnlyList<EffortOption> _effortOptions = Array.Empty<EffortOption>();

    // One instance per level, so the dropdown keeps its selection when the
    // list is rebuilt.
    private readonly Dictionary<ClaudeEffort, EffortOption> _effortOptionCache = new();

    private EffortOption OptionOf(ClaudeEffort effort)
    {
        if (!_effortOptionCache.TryGetValue(effort, out var option))
            _effortOptionCache[effort] = option = new EffortOption(effort);
        return option;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDisplay))]
    private ModelOption _selectedModel = null!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEffortOption))]
    private ClaudeEffort _selectedEffort = ClaudeEffort.Default;

    /// <summary>The dropdown's view of <see cref="SelectedEffort"/>.</summary>
    public EffortOption SelectedEffortOption
    {
        get => OptionOf(SelectedEffort);
        // Null while the dropdown swaps its list; not a choice.
        set { if (value is not null) SelectedEffort = value.Value; }
    }

    /// <summary>
    /// False for a model the CLI reports without effort levels (Haiku 4.5 on
    /// CLI 2.1.296). The dropdown is then disabled rather than offering levels
    /// the CLI would drop.
    /// </summary>
    public bool HasEffort => SelectedModel?.SupportedEfforts is not { Count: 0 };

    /// <summary>
    /// The level the CLI uses, as it reports it. Null while not known, and for
    /// a model without effort.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffortTooltip))]
    private ClaudeEffort? _appliedEffort;

    public string EffortTooltip =>
        !HasEffort
            ? "This model has no reasoning effort setting."
            : "Reasoning effort. Default sends no level to the CLI."
              + (AppliedEffort is { } applied ? $"\nIn use: {applied.ToCliValue()}" : "")
              + "\nTakes effect on the next request, also during a reply.";

    /// <summary>
    /// Set while seeding the pickers from saved settings, so restoring a value
    /// does not look like the user picking it and needlessly reach the CLI.
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
        RefreshEffortOptions();
        OnPropertyChanged(nameof(ShowFastMode));
        OnPropertyChanged(nameof(FastModeTooltip));
        if (_suppressModelEffortApply) return;

        ApplyModelAndEffort();
        // A preview taken for the previous choice no longer applies. This also
        // covers the case the service cannot signal: no model reported yet, so
        // clearing it raises nothing.
        RefreshModelPreview();
    }

    partial void OnSelectedEffortChanged(ClaudeEffort value)
    {
        RefreshEffortOptions();
        RefreshDefaultEffortLabel();
        ApplyModelAndEffort();
    }

    partial void OnModelIdChanged(string? value) => RefreshResolvedModelLabel();

    partial void OnAppliedEffortChanged(ClaudeEffort? value)
    {
        RefreshDefaultEffortLabel();
        RefreshPicker();
    }

    private void RefreshEffortOptions()
    {
        var levels = SelectedModel?.SupportedEfforts
            ?? ((ClaudeEffort[])Enum.GetValues(typeof(ClaudeEffort))).Where(e => e != ClaudeEffort.Default).ToList();

        var shown = new[] { ClaudeEffort.Default }
            .Concat(levels)
            .Append(SelectedEffort)
            .Distinct()
            .OrderBy(e => e)
            .Select(OptionOf)
            .ToList();

        if (!shown.SequenceEqual(_effortOptions))
        {
            _effortOptions = shown;
            OnPropertyChanged(nameof(EffortOptions));
            // The dropdown drops its selection when its list is replaced.
            OnPropertyChanged(nameof(SelectedEffortOption));
        }

        OnPropertyChanged(nameof(HasEffort));
        OnPropertyChanged(nameof(EffortTooltip));
        RefreshPicker();
    }

    /// <summary>
    /// Names the level Default stands for, as the model row names the model:
    /// "Default (medium)". Only while the CLI has said.
    /// </summary>
    private void RefreshDefaultEffortLabel() =>
        OptionOf(ClaudeEffort.Default).DisplayName = AppliedEffort is { } applied && SelectedEffort == ClaudeEffort.Default
            ? $"Default ({applied.ToCliValue()})"
            : EffortOption.DefaultLabel;

    // ── One picker for model, effort and Fast mode ────────────────────────
    // The status bar shows a single button, "Opus 5.5 · Medium", that opens a
    // panel with all three. The dropdown lists above stay the source; this is
    // their view for the panel.

    /// <summary>The CLI's aliases (Default, Opus …), which follow the newest model of their family.</summary>
    public IReadOnlyList<ModelOption> CurrentModelOptions => _modelOptions.Where(m => !m.IsPinnedVersion).ToList();

    /// <summary>Fixed model versions the CLI also offers, under "Other models".</summary>
    public IReadOnlyList<ModelOption> OtherModelOptions => _modelOptions.Where(m => m.IsPinnedVersion).ToList();

    public bool HasOtherModels => _modelOptions.Any(m => m.IsPinnedVersion);

    public string OtherModelsLabel => $"Other models ({_modelOptions.Count(m => m.IsPinnedVersion)})";

    /// <summary>
    /// Whether the panel lists the fixed versions. Opens by itself when one of
    /// them is the session's model, so the check mark is never hidden.
    /// </summary>
    [ObservableProperty]
    private bool _showOtherModels;

    /// <summary>What the button reads: the model, and the effort in use when the model has one.</summary>
    public string ModelPickerLabel
    {
        get
        {
            var model = SelectedModel?.DisplayName ?? ModelOption.UnresolvedLabel;
            if (!HasEffort) return model;

            var effort = SelectedEffort != ClaudeEffort.Default
                ? SelectedEffort.ToString()
                : AppliedEffort?.ToString() ?? EffortOption.DefaultLabel;
            return $"{model} · {effort}";
        }
    }

    public string ModelPickerTooltip =>
        $"Model: {ModelDisplay}\nModel, reasoning effort and Fast mode for this chat.\n"
        + "Changes take effect on the next request, also during a reply.";

    /// <summary>Under the effort buttons: what Default stands for, or why a level is faded.</summary>
    public string EffortHint
    {
        get
        {
            if (SelectedEffort == ClaudeEffort.Default)
                return AppliedEffort is { } applied
                    ? $"Default = {applied}, as reported by Claude Code."
                    : "Default leaves the level to Claude Code.";

            return SelectedModel?.SupportedEfforts is { } levels && !levels.Contains(SelectedEffort)
                ? $"This model has no {SelectedEffort} level. Claude Code uses a lower one."
                : "";
        }
    }

    [RelayCommand]
    private void PickModel(ModelOption? option)
    {
        if (option is not null) SelectedModel = option;
    }

    [RelayCommand]
    private void PickEffort(EffortOption? option)
    {
        if (option is not null) SelectedEffort = option.Value;
    }

    /// <summary>Brings the panel's marks and the button's text up to date.</summary>
    private void RefreshPicker()
    {
        foreach (var option in _modelOptions)
            option.IsSelected = ReferenceEquals(option, SelectedModel);

        var levels = SelectedModel?.SupportedEfforts;
        foreach (var option in _effortOptions)
        {
            option.IsSelected = option.Value == SelectedEffort;
            option.IsSupported = option.Value == ClaudeEffort.Default || levels is null || levels.Contains(option.Value);
        }

        if (SelectedModel is { IsPinnedVersion: true })
            ShowOtherModels = true;

        OnPropertyChanged(nameof(ModelPickerLabel));
        OnPropertyChanged(nameof(ModelPickerTooltip));
        OnPropertyChanged(nameof(EffortHint));
    }

    private void RaiseModelListsChanged()
    {
        OnPropertyChanged(nameof(CurrentModelOptions));
        OnPropertyChanged(nameof(OtherModelOptions));
        OnPropertyChanged(nameof(HasOtherModels));
        OnPropertyChanged(nameof(OtherModelsLabel));
    }

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
        _modelOptionsSource = ClaudeModelCatalog.Reported;
        _modelOptions = BuildModelOptions(_modelOptionsSource);

        _suppressModelEffortApply = true;
        try
        {
            SelectedModel = OptionFor(options.Model);
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

        chatService.AppliedEffortChanged += OnChatServiceAppliedEffortChanged;
        AppliedEffort = chatService.AppliedEffort;

        chatService.FastModeChanged += OnChatServiceFastModeChanged;
        FastModeStatus = chatService.FastMode;
        IsFastModeOn = chatService.FastModeRequested;

        ClaudeModelCatalog.ReportedChanged += OnReportedModelsChanged;
        // The list may have arrived between building ours and subscribing.
        if (!ReferenceEquals(_modelOptionsSource, ClaudeModelCatalog.Reported))
            RebuildModelOptions();
    }

    private void OnChatServiceAppliedEffortChanged(ClaudeEffort? effort) => Dispatch(() => AppliedEffort = effort);

    private void OnChatServiceFastModeChanged(FastModeStatus status) => Dispatch(() => FastModeStatus = status);

    // ── Fast mode ─────────────────────────────────────────────────────────

    /// <summary>
    /// The user's switch. For this session only: fast mode costs more, so a
    /// new session does not inherit it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFastMode))]
    [NotifyPropertyChangedFor(nameof(CanToggleFastMode))]
    [NotifyPropertyChangedFor(nameof(FastModeTooltip))]
    private bool _isFastModeOn;

    /// <summary>What the CLI reports, which can differ from the switch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleFastMode))]
    [NotifyPropertyChangedFor(nameof(FastModeTooltip))]
    private FastModeStatus _fastModeStatus = FastModeStatus.Unknown;

    partial void OnIsFastModeOnChanged(bool value)
    {
        try { _chatService?.SetFastMode(value); }
        catch (Exception ex) { _logger.LogError(ex, "[VM] Could not change fast mode"); }
    }

    /// <summary>
    /// Shown for a model the CLI offers fast mode for, and while it is on, so
    /// it can be turned off after a switch to a model without it.
    /// </summary>
    public bool ShowFastMode => SelectedModel?.SupportsFastMode == true || IsFastModeOn;

    /// <summary>
    /// Off only when something outside VsAgentic blocks fast mode and it is
    /// not on; turning it off always works.
    /// </summary>
    public bool CanToggleFastMode => IsFastModeOn || !FastModeStatus.IsBlocked;

    public string FastModeTooltip
    {
        get
        {
            var text = "Fast mode: faster output from the same model, billed at a higher rate.\n"
                       + "For this session only. Takes effect on the next request.";

            if (FastModeStatus.BlockedText is { } blocked)
                return text + "\n\n" + blocked;
            if (!IsFastModeOn)
                return text;
            if (FastModeStatus.IsCoolingDown)
                return text + "\n\nPaused after a rate limit. Requests run at standard speed until it resumes.";
            if (SelectedModel?.SupportsFastMode == false)
                return text + "\n\nThe selected model has no fast mode. It applies again on a model that has.";
            if (FastModeStatus.IsOn)
                return text + "\n\nOn.";
            return text;
        }
    }

    private void OnReportedModelsChanged(IReadOnlyList<ClaudeModelInfo> models) => Dispatch(RebuildModelOptions);

    /// <summary>
    /// Replaces the rows with the list the CLI reported, keeping the selection
    /// on the same alias. Not a user choice, so nothing is sent to the CLI.
    /// </summary>
    private void RebuildModelOptions()
    {
        var alias = SelectedModel?.Alias ?? "";

        _suppressModelEffortApply = true;
        try
        {
            _modelOptionsSource = ClaudeModelCatalog.Reported;
            _modelOptions = BuildModelOptions(_modelOptionsSource);
            OnPropertyChanged(nameof(ModelOptions));
            RaiseModelListsChanged();
            SelectedModel = OptionFor(alias);
        }
        finally
        {
            _suppressModelEffortApply = false;
        }
    }

    /// <summary>
    /// The picker row for a configured alias, adding one when the alias is not
    /// in the catalog. The Model setting is free text under Tools → Options, and
    /// the host passes whatever it holds to <c>--model</c>. Folding an unknown
    /// value back to Default, as the catalog does, would leave the picker naming
    /// a model the CLI is not being asked for: with a typo every message fails
    /// while the status bar reads Default, and nothing points at the setting.
    /// A row of its own names what is actually being sent.
    /// </summary>
    private ModelOption OptionFor(string? configuredAlias)
    {
        var alias = (configuredAlias ?? "").Trim();

        var known = _modelOptions.FirstOrDefault(
            m => string.Equals(m.Alias, alias, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known;
        if (alias.Length == 0) return _modelOptions[0];

        // A full model id ("claude-opus-5", a fixed version picked from the
        // CLI's list before the CLI has answered this time) gets its readable
        // name, as the button shows it. Anything else stays as typed, so a typo
        // in the setting is still visible.
        var name = alias.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)
            ? ClaudeModelCatalog.DisplayNameFor(alias)
            : alias;
        var custom = new ModelOption(new ClaudeModelInfo(alias, name));
        _modelOptions.Add(custom);
        RaiseModelListsChanged();
        return custom;
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
        RefreshPicker();
    }

    // ── Status bar text ───────────────────────────────────────────────────

    /// <summary>What the model tooltip names: what is running, when that is known.</summary>
    public string ModelDisplay => ModelId is { Length: > 0 }
        ? ClaudeModelCatalog.DisplayNameFor(ModelId)
        : SelectedModel?.DisplayName ?? ClaudeModelCatalog.Default.DisplayName;

    /// <summary>
    /// The window the CLI reported for this session's model. Before it has,
    /// the window is estimated from the model id, which only recognizes the
    /// <c>[1m]</c> suffix and so reads too low for models with a long default.
    /// </summary>
    public long ContextWindowTokens =>
        Usage.ContextWindowTokens ?? ClaudeModelCatalog.ContextWindowFor(ModelId);

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

    // Each window shows the plan limit the CLI reported when there is one, and
    // the local estimate otherwise: before the first turn, with a CLI that
    // cannot report, or after the window has reset.

    public string ShortWindowDisplay => Usage.ShortWindowLimit is { } limit
        ? FormatPercent(limit.Percent)
        : FormatBudget(Usage.ShortWindowTokens, Usage.ShortWindowBudget);

    public string ShortWindowTooltip => Usage.ShortWindowLimit is { } limit
        ? LimitTooltip("5-hour limit", limit, Array.Empty<PlanLimitWindow>())
        : WindowTooltip("Last 5 hours", Usage.ShortWindowTokens, Usage.ShortWindowBudget, Usage.ShortWindowFraction);

    public UsageLevel ShortWindowLevel => Usage.ShortWindowLevel;

    public string LongWindowDisplay => Usage.LongWindowLimit is { } limit
        ? FormatPercent(limit.Percent)
        : FormatBudget(Usage.LongWindowTokens, Usage.LongWindowBudget);

    public string LongWindowTooltip => Usage.LongWindowLimit is { } limit
        ? LimitTooltip("Weekly limit", limit, Usage.ModelLimits)
        : WindowTooltip("Last 7 days", Usage.LongWindowTokens, Usage.LongWindowBudget, Usage.LongWindowFraction);

    public UsageLevel LongWindowLevel => Usage.LongWindowLevel;

    private static string WindowTooltip(string title, long used, long budget, double fraction) =>
        title + "\n"
        + $"Used: {FormatExact(used)} of about {FormatExact(budget)} tokens ({fraction:P0})\n"
        + "Counted across every VsAgentic session on this machine, and kept between restarts.\n"
        + "The budget is an estimate — Anthropic does not publish the real limit, so treat this "
        + "as a gauge rather than an authority. Adjust it under Tools → Options → VsAgentic.\n"
        + "On a subscription, the limit Claude Code reports replaces this estimate after the first reply.";

    private static string LimitTooltip(string title, PlanLimitWindow limit, IReadOnlyList<PlanLimitWindow> models)
    {
        var text = title + "\n"
            + $"Used: {FormatPercent(limit.Percent)} of your plan's limit\n";
        if (limit.ResetsAt is { } resetsAt)
            text += $"Resets: {FormatResetTime(resetsAt)}\n";

        foreach (var model in models)
        {
            text += $"{model.Name}: {FormatPercent(model.Percent)}";
            if (model.ResetsAt is { } modelReset) text += $", resets {FormatResetTime(modelReset)}";
            text += "\n";
        }

        return text
            + "Reported by Claude Code for your account, so it includes use outside VsAgentic, "
            + "such as the terminal and claude.ai.";
    }

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

    /// <summary>Whole percent: "13%". The CLI reports whole numbers too.</summary>
    private static string FormatPercent(double percent) =>
        Math.Round(percent).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Local time, with the day added once the reset is more than a day out:
    /// "11:49", or "Sat 16:00" for the weekly window.
    /// </summary>
    private static string FormatResetTime(DateTimeOffset resetsAt)
    {
        var local = resetsAt.ToLocalTime();
        var format = local - DateTimeOffset.Now > TimeSpan.FromHours(24) ? "ddd HH:mm" : "HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

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
        Description = info.Description;
        SupportedEfforts = info.SupportedEfforts;
        SupportsFastMode = info.SupportsFastMode;
        _displayName = info.IsCliDefault ? UnresolvedLabel : info.DisplayName;
        RowName = info.DisplayName;
    }

    public string Alias { get; }

    /// <summary>
    /// The name on the row in the model panel. Fixed, unlike
    /// <see cref="DisplayName"/>: the Default row keeps the CLI's own name
    /// ("Default (recommended)") rather than taking the model it resolves to,
    /// which would show the same model twice. Its description names that model.
    /// </summary>
    public string RowName { get; }

    /// <summary>
    /// A fixed model version ("claude-opus-4-8") rather than one of the CLI's
    /// aliases ("opus"), which move to the newest model of their family. The
    /// CLI lists the aliases first and the fixed versions after them; the
    /// picker puts the fixed ones under "Other models".
    /// </summary>
    public bool IsPinnedVersion => Alias.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is the session's model. Drives the check mark in the picker.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The CLI's description of the model, shown as the row's tooltip.</summary>
    public string? Description { get; }

    /// <inheritdoc cref="ClaudeModelInfo.SupportedEfforts"/>
    public IReadOnlyList<ClaudeEffort>? SupportedEfforts { get; }

    /// <inheritdoc cref="ClaudeModelInfo.SupportsFastMode"/>
    public bool SupportsFastMode { get; }

    public bool IsCliDefault => Alias.Length == 0;

    [ObservableProperty]
    private string _displayName;

    public override string ToString() => DisplayName;
}

/// <summary>
/// One row of the effort dropdown. An object rather than the bare enum for
/// the same reason as <see cref="ModelOption"/>: the Default row names the
/// level the CLI uses once it has said.
/// </summary>
public partial class EffortOption : ObservableObject
{
    public const string DefaultLabel = "Default";

    public EffortOption(ClaudeEffort value)
    {
        Value = value;
        _displayName = value == ClaudeEffort.Default ? DefaultLabel : value.ToString();
    }

    public ClaudeEffort Value { get; }

    /// <summary>The level's name alone, for the picker's buttons.</summary>
    public string ShortName => Value == ClaudeEffort.Default ? DefaultLabel : Value.ToString();

    /// <summary>Whether this is the session's level.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// False for a level the selected model does not take. It stays in the
    /// list while selected, so switching models does not change the user's
    /// choice; the CLI lowers it.
    /// </summary>
    [ObservableProperty]
    private bool _isSupported = true;

    [ObservableProperty]
    private string _displayName;

    public override string ToString() => DisplayName;
}
