using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using VsAgentic.Services.Abstractions;

namespace VsAgentic.VSExtension.ToolWindows;

/// <summary>
/// One labelled gauge in the chat header: a caption, a reading, and a thin bar
/// that changes colour as it fills.
/// </summary>
public partial class UsageMeter : UserControl
{
    public UsageMeter()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(UsageMeter),
            new PropertyMetadata(""));

    /// <summary>Short label above the bar, e.g. "Context".</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public static readonly DependencyProperty ValueTextProperty =
        DependencyProperty.Register(nameof(ValueText), typeof(string), typeof(UsageMeter),
            new PropertyMetadata(""));

    /// <summary>The reading itself, e.g. "128k / 200k". Pre-formatted by the view model.</summary>
    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public static readonly DependencyProperty FractionProperty =
        DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(UsageMeter),
            new PropertyMetadata(0d));

    /// <summary>How full the bar is, 0 to 1. Already clamped by <see cref="SessionUsage"/>.</summary>
    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(UsageLevel), typeof(UsageMeter),
            new PropertyMetadata(UsageLevel.Normal));

    /// <summary>Drives the bar colour; see the triggers in the XAML.</summary>
    public UsageLevel Level
    {
        get => (UsageLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public static readonly DependencyProperty ShowBarProperty =
        DependencyProperty.Register(nameof(ShowBar), typeof(bool), typeof(UsageMeter),
            new PropertyMetadata(true));

    /// <summary>
    /// False for readings with no ceiling — session totals only ever grow, so
    /// there is no fraction to draw.
    /// </summary>
    public bool ShowBar
    {
        get => (bool)GetValue(ShowBarProperty);
        set => SetValue(ShowBarProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWarningBrush();
        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // VSColorTheme is a shell-lifetime static, so a meter that forgot to
        // detach would keep this control (and the whole session) alive.
        VSColorTheme.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(ThemeChangedEventArgs e) =>
        Dispatcher.BeginInvoke(new Action(ApplyWarningBrush));

    /// <summary>
    /// Visual Studio exposes no semantic "warning" brush — only a validation
    /// error one — so the amber is picked here against the current theme rather
    /// than hard-coded: a single value legible on both Dark and Light does not
    /// exist.
    /// </summary>
    private void ApplyWarningBrush()
    {
        try
        {
            var background = VSColorTheme.GetThemedColor(
                EnvironmentColors.ToolWindowBackgroundColorKey);

            var amber = IsDark(background)
                ? Color.FromRgb(0xE0, 0xA3, 0x3E)   // lifted, for dark backgrounds
                : Color.FromRgb(0xA9, 0x6A, 0x00);  // deepened, for light ones

            var brush = new SolidColorBrush(amber);
            brush.Freeze();
            Resources["UsageWarningBrush"] = brush;
        }
        catch (Exception)
        {
            // Falling back to the accent colour costs a shade of meaning, not
            // the meter — and never the chat.
        }
    }

    /// <summary>
    /// Rec. 601 luma. Takes a <see cref="System.Drawing.Color"/> because that is
    /// what <see cref="VSColorTheme.GetThemedColor"/> hands back — WPF's own
    /// Color type is a different struct.
    /// </summary>
    private static bool IsDark(System.Drawing.Color c) =>
        (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
}
