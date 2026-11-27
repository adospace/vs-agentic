using System.ComponentModel;
using System.Drawing.Design;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Diagnostics;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// The setup check opened from Tools → Options: the report as plain text,
/// with Copy for a bug report and Run again after a fix. The same check runs
/// as <c>/doctor</c> in a chat, where it also covers that chat's session.
/// </summary>
internal sealed class SetupCheckDialog : DialogWindow
{
    private readonly TextBox _text;
    private readonly Button _copy;
    private readonly Button _again;
    private readonly CancellationTokenSource _cts = new();
    private SetupCheckReport? _report;

    public SetupCheckDialog()
    {
        Title = "VsAgentic setup check";
        Width = 760;
        Height = 520;
        MinWidth = 420;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        HasMinimizeButton = false;
        SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);

        _text = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Segoe UI, Segoe UI Emoji"),
            FontSize = 12,
            Padding = new Thickness(6),
            Text = "Checking the setup…",
        };
        _text.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
        _text.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
        _text.SetResourceReference(BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

        _copy = MakeButton("_Copy", (_, _) => CopyReport());
        _again = MakeButton("_Run again", (_, _) => Run());
        var close = MakeButton("Close", (_, _) => Close());
        close.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(_copy);
        buttons.Children.Add(_again);
        buttons.Children.Add(close);

        var root = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_text);
        Content = root;

        Loaded += (_, _) => Run();
        Closed += (_, _) => _cts.Cancel();
    }

    private static Button MakeButton(string label, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 86,
            Height = 24,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
        };
        button.Click += click;
        return button;
    }

    private void Run()
    {
        _copy.IsEnabled = false;
        _again.IsEnabled = false;
        _text.Text = "Checking the setup…";

        _ = RunAsync();
    }

    // Started on the dialog's thread, so it resumes there after the check.
    private async System.Threading.Tasks.Task RunAsync()
    {
        string text;
        try
        {
            var task = VsAgenticPackage.RunSetupCheckFromOptionsAsync(_cts.Token);
            if (task is null)
            {
                text = "VsAgentic is not loaded yet. Open the VsAgentic session list, then try again.";
                _report = null;
            }
            else
            {
                _report = await task;
                text = _report.ToPlainText();
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _report = null;
            text = "The setup check could not finish: " + ex.Message;
        }

        if (_cts.IsCancellationRequested) return;
        _text.Text = text;
        _copy.IsEnabled = _report is not null;
        _again.IsEnabled = true;
    }

    /// <summary>Markdown, for pasting into a GitHub issue.</summary>
    private void CopyReport()
    {
        if (_report is null) return;
        try
        {
            Clipboard.SetText(_report.ToMarkdown());
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another program for a moment.
            System.Diagnostics.Debug.WriteLine($"VsAgentic: could not copy the setup check: {ex.Message}");
        }
    }
}

/// <summary>
/// Shows the "…" button on the Check setup row of the options grid; a click
/// opens <see cref="SetupCheckDialog"/>.
/// </summary>
internal sealed class SetupCheckEditor : UITypeEditor
{
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext? context) => UITypeEditorEditStyle.Modal;

    public override object? EditValue(ITypeDescriptorContext? context, IServiceProvider? provider, object? value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        new SetupCheckDialog().ShowModal();
        return value;
    }
}

/// <summary>
/// Keeps the Check setup row's text from being edited in the grid; the
/// "…" button stays. Without a grid (DialogPage storing its settings) it
/// converts as a plain string.
/// </summary>
internal sealed class SetupCheckConverter : StringConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        context?.PropertyDescriptor is null && base.CanConvertFrom(context, sourceType);
}
