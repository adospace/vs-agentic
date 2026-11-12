using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Agents;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// Tools › Options › VsAgentic › Prices: the price list the split plan's
/// estimate uses, and the earlier runs that correct it.
///
/// Like the Agents page, this page stores nothing of its own: the values live
/// on the General page (<see cref="VsAgenticOptionsPage.ModelPrices"/>), so the
/// chat reads them from one place.
/// </summary>
[ComVisible(true)]
[Guid("b6f3e2a1-5d47-4c8e-a913-2e7f0c5d8a64")]
public sealed class PricesOptionsPage : UIElementDialogPage
{
    private PricesOptionsControl? _control;

    protected override UIElement Child => _control ??= new PricesOptionsControl();

    private static VsAgenticOptionsPage? GeneralPage =>
        VsAgenticPackage.Instance?.GetGeneralOptionsPage();

    // Loaded once per opening of the dialog: going to another page and back
    // must keep what the user typed here.
    private bool _loaded;

    protected override void OnActivate(CancelEventArgs e)
    {
        base.OnActivate(e);
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_loaded) return;
        ((PricesOptionsControl)Child).Load(GeneralPage);
        _loaded = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _loaded = false;
        base.OnClosed(e);
    }

    protected override void OnApply(PageApplyEventArgs e)
    {
        if (e.ApplyBehavior == ApplyKind.Apply && _control is not null)
        {
            if (!_control.Apply(GeneralPage, out var error))
            {
                e.ApplyBehavior = ApplyKind.CancelNoNavigate;
                MessageBox.Show(error, "VsAgentic", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        base.OnApply(e);
    }

    public override void SaveSettingsToStorage() { }
    public override void LoadSettingsFromStorage() { }
}

/// <summary>The content of <see cref="PricesOptionsPage"/>.</summary>
internal sealed class PricesOptionsControl : ScrollViewer
{
    private static readonly string[] Columns = { "Input", "Output", "Cache read", "Cache write", "Output tokens/s" };

    private readonly Dictionary<string, TextBox[]> _cells = new Dictionary<string, TextBox[]>();
    private readonly TextBox _source = new TextBox { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _runs = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
    private readonly Button _forget = new Button { Content = "Forget earlier runs", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left };
    private bool _forgetRuns;

    public PricesOptionsControl()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(Heading("Model prices"));
        panel.Children.Add(Note("US dollars per million tokens. The plan card for splitting a task across agents uses them to compare working alone with working with agents. Cache write is the 5-minute cache. The speed is only for the time estimate. Nothing is sent anywhere, and Claude Code bills by its own prices: change a value here when Anthropic changes a price."));

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        for (var c = 0; c < Columns.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(c == Columns.Length - 1 ? 130 : 92) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < Columns.Length; c++)
            Place(grid, new TextBlock { Text = Columns[c], FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 4), TextWrapping = TextWrapping.Wrap }, 0, c + 1);

        var row = 1;
        foreach (var family in ModelPriceList.Families)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Place(grid, new TextBlock { Text = Capitalize(family), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 6, 2) }, row, 0);
            var boxes = new TextBox[Columns.Length];
            for (var c = 0; c < Columns.Length; c++)
            {
                boxes[c] = new TextBox { Margin = new Thickness(0, 2, 6, 2), TextAlignment = TextAlignment.Right };
                System.Windows.Automation.AutomationProperties.SetName(boxes[c], $"{Capitalize(family)} {Columns[c]}");
                Place(grid, boxes[c], row, c + 1);
            }
            _cells[family] = boxes;
            row++;
        }
        panel.Children.Add(grid);

        panel.Children.Add(Heading("Source"));
        panel.Children.Add(Note("Where the prices come from and when you checked them. Shown nowhere else; for you."));
        panel.Children.Add(_source);

        var reset = new Button { Content = "Reset to built-in prices", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => Show(ModelPriceList.Defaults);
        panel.Children.Add(reset);

        panel.Children.Add(Heading("Earlier runs"));
        panel.Children.Add(Note("After a plan runs, its measured cost and time correct the next estimate, and they decide how easily the chat suggests splitting a task. The last " + SplitCalibration.Keep + " runs count."));
        panel.Children.Add(_runs);
        _forget.Click += (_, _) =>
        {
            _forgetRuns = true;
            _runs.Text = "Forgotten when you press OK.";
            _forget.IsEnabled = false;
        };
        panel.Children.Add(_forget);

        Content = panel;
    }

    public void Load(VsAgenticOptionsPage? general)
    {
        _forgetRuns = false;
        IsEnabled = general is not null;
        Show(ModelPriceList.Parse(general?.ModelPrices, general?.ModelPricesSource));

        var calibration = SplitCalibration.Parse(general?.SplitRuns);
        _forget.IsEnabled = calibration.Runs > 0;
        _runs.Text = calibration.Runs == 0
            ? "No plan has run yet."
            : string.Format(CultureInfo.InvariantCulture,
                  "{0} {1}. Measured cost is {2:0.##} × the estimate and time {3:0.##} × the estimate (medians).",
                  calibration.Runs, calibration.Runs == 1 ? "run" : "runs", calibration.CostFactor, calibration.TimeFactor)
              + (calibration.MeasuredSaving is double s
                  ? string.Format(CultureInfo.InvariantCulture, " Against the estimate for working alone, splitting {0}.",
                      s >= 0 ? $"saved {Math.Round(s * 100):0} %" : $"cost {Math.Round(-s * 100):0} % more")
                  : "");
    }

    private void Show(ModelPriceList prices)
    {
        foreach (var p in prices.All)
        {
            var b = _cells[p.Family];
            b[0].Text = Money(p.Input);
            b[1].Text = Money(p.Output);
            b[2].Text = Money(p.CacheRead);
            b[3].Text = Money(p.CacheWrite);
            b[4].Text = p.OutputTokensPerSecond.ToString(CultureInfo.InvariantCulture);
        }
        _source.Text = prices.Source;
    }

    /// <summary>Stores the list. False, with a message, when a value is not a number.</summary>
    public bool Apply(VsAgenticOptionsPage? general, out string error)
    {
        error = "";
        if (general is null) return true;

        var prices = new List<ModelPrice>();
        foreach (var family in ModelPriceList.Families)
        {
            var b = _cells[family];
            var values = new decimal[4];
            for (var c = 0; c < 4; c++)
            {
                if (!ModelPriceList.TryMoney(b[c].Text, out values[c]))
                {
                    error = $"{Capitalize(family)} {Columns[c].ToLowerInvariant()}: \"{b[c].Text}\" is not a price. Use a number with a dot, such as 0.25.";
                    return false;
                }
            }
            if (!int.TryParse(b[4].Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed) || speed <= 0)
            {
                error = $"{Capitalize(family)} output tokens/s: \"{b[4].Text}\" is not a whole number above 0.";
                return false;
            }
            prices.Add(new ModelPrice(family, values[0], values[1], values[2], values[3], speed));
        }

        var list = new ModelPriceList(prices, _source.Text.Trim());
        // The built-in list is stored as empty, so a later release can update it.
        var same = list.Serialize() == ModelPriceList.Defaults.Serialize();
        general.ModelPrices = same ? "" : list.Serialize();
        general.ModelPricesSource = same && list.Source == ModelPriceList.DefaultSource ? "" : list.Source;
        if (_forgetRuns) general.SplitRuns = "";
        general.SaveSettingsToStorage();
        return true;
    }

    // Every digit the list holds: a rounded value would be saved on OK.
    private static string Money(decimal value)
    {
        var plain = ModelPriceList.Plain(value);
        return plain.Contains(".") ? (plain.Length - plain.IndexOf('.') - 1 < 2 ? plain + "0" : plain) : plain + ".00";
    }

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    private static TextBlock Heading(string text) =>
        new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) };

    private static TextBlock Note(string text) =>
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 2) };
}
