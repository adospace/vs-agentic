using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VsAgentic.Services.ClaudeCli.Permissions;
using VsAgentic.Services.ClaudeCli.Questions;

namespace VsAgentic.UI.Controls;

/// <summary>
/// Builds the WPF visuals for the in-chat permission banner and question card
/// without needing dedicated XAML files. The chat host (ChatWebView) inserts
/// the produced FrameworkElement into its banner row, and invokes the
/// supplied callbacks when the user clicks Allow / Deny / Submit.
/// </summary>
internal static class ChatBannerBuilder
{
    public static FrameworkElement BuildPermissionBanner(
        PermissionRequest request,
        Action<PermissionDecision> onResolved)
    {
        var theme = BannerTheme.Current;

        var root = new StackPanel
        {
            Background = theme.Background,
            Margin = new Thickness(0),
        };

        var header = new TextBlock
        {
            Text = $"Claude wants to use {request.ToolName}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = theme.Foreground,
            Margin = new Thickness(12, 10, 12, 4),
        };
        root.Children.Add(header);

        var bodyText = FormatPermissionBody(request);
        if (!string.IsNullOrEmpty(bodyText))
        {
            root.Children.Add(new TextBlock
            {
                Text = bodyText,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo, monospace"),
                FontSize = 12,
                Foreground = theme.Muted,
                Margin = new Thickness(12, 0, 12, 8),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 160,
            });
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 12, 10),
        };

        var allowBtn = MakeButton("Allow", theme.Accent, theme.AccentForeground);
        allowBtn.Click += (_, __) =>
        {
            var inputJson = request.Input.ValueKind == JsonValueKind.Undefined ? "{}" : request.Input.GetRawText();
            onResolved(PermissionDecision.Allow(inputJson));
        };
        var denyBtn = MakeButton("Deny", theme.Danger, theme.DangerForeground);
        denyBtn.Click += (_, __) => onResolved(PermissionDecision.Deny("User denied this action"));

        buttons.Children.Add(allowBtn);
        buttons.Children.Add(denyBtn);
        root.Children.Add(buttons);

        return root;
    }

    public static FrameworkElement BuildQuestionCard(
        UserQuestionRequest request,
        Action<IReadOnlyDictionary<string, string>> onSubmitted)
    {
        var theme = BannerTheme.Current;

        var root = new StackPanel
        {
            Background = theme.Background,
        };

        root.Children.Add(new TextBlock
        {
            Text = "Claude needs more information",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = theme.Foreground,
            Margin = new Thickness(12, 10, 12, 8),
        });

        // For each question, render the prompt + a list of radio buttons
        // (single-select) or checkboxes (multi-select), plus a free-text entry
        // labeled "Other" that lets the user type a custom answer.
        var perQuestionState = new List<Func<string>>();

        foreach (var q in request.Questions)
        {
            var groupName = "g_" + Guid.NewGuid().ToString("N");

            root.Children.Add(new TextBlock
            {
                Text = q.Question,
                FontWeight = FontWeights.SemiBold,
                Foreground = theme.Foreground,
                Margin = new Thickness(12, 6, 12, 4),
                TextWrapping = TextWrapping.Wrap,
            });

            var optionsPanel = new StackPanel { Margin = new Thickness(20, 0, 12, 4) };
            var radios = new List<RadioButton>();
            var checks = new List<CheckBox>();

            foreach (var opt in q.Options)
            {
                if (q.MultiSelect)
                {
                    var cb = new CheckBox
                    {
                        Content = MakeOptionContent(opt.Label, opt.Description, theme),
                        Foreground = theme.Foreground,
                        Margin = new Thickness(0, 2, 0, 2),
                    };
                    checks.Add(cb);
                    optionsPanel.Children.Add(cb);
                }
                else
                {
                    var rb = new RadioButton
                    {
                        Content = MakeOptionContent(opt.Label, opt.Description, theme),
                        GroupName = groupName,
                        Foreground = theme.Foreground,
                        Margin = new Thickness(0, 2, 0, 2),
                    };
                    radios.Add(rb);
                    optionsPanel.Children.Add(rb);
                }
            }

            // Free-text "Other" row.
            var otherBox = new TextBox
            {
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(4, 2, 4, 2),
                MinWidth = 200,
                MaxWidth = 480,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = theme.InputBackground,
                Foreground = theme.Foreground,
                BorderBrush = theme.Border,
            };
            // Placeholder via tooltip — net472 WPF lacks PlaceholderText.
            otherBox.ToolTip = "Type your own answer (overrides selection)";
            optionsPanel.Children.Add(new TextBlock
            {
                Text = "Or type your own:",
                Foreground = theme.Muted,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 2),
            });
            optionsPanel.Children.Add(otherBox);

            root.Children.Add(optionsPanel);

            // Capture for submit:
            var capturedQuestion = q;
            var capturedRadios = radios;
            var capturedChecks = checks;
            var capturedOther = otherBox;
            perQuestionState.Add(() =>
            {
                var typed = capturedOther.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(typed)) return typed;
                if (capturedQuestion.MultiSelect)
                {
                    var picked = capturedChecks
                        .Where(c => c.IsChecked == true)
                        .Select((c, i) => capturedQuestion.Options[capturedChecks.IndexOf(c)].Label)
                        .ToList();
                    return string.Join(", ", picked);
                }
                else
                {
                    var idx = capturedRadios.FindIndex(r => r.IsChecked == true);
                    return idx >= 0 && idx < capturedQuestion.Options.Count
                        ? capturedQuestion.Options[idx].Label
                        : "";
                }
            });
        }

        var submitRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 4, 12, 10),
        };
        var submitBtn = MakeButton("Submit", theme.Accent, theme.AccentForeground);
        submitBtn.Click += (_, __) =>
        {
            var answers = new Dictionary<string, string>();
            for (int i = 0; i < request.Questions.Count; i++)
                answers[request.Questions[i].Question] = perQuestionState[i]();
            onSubmitted(answers);
        };
        submitRow.Children.Add(submitBtn);
        root.Children.Add(submitRow);

        return root;
    }

    private static FrameworkElement MakeOptionContent(string label, string description, BannerTheme theme)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };
        sp.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = theme.Foreground,
            FontWeight = FontWeights.SemiBold,
        });
        if (!string.IsNullOrEmpty(description))
        {
            sp.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = theme.Muted,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        return sp;
    }

    private static Button MakeButton(string text, Brush background, Brush foreground)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        border.Name = "bd";

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.MarginProperty, new Thickness(14, 6, 14, 6));
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hoverBg = Darken(background, 0.15);
        var pressedBg = Darken(background, 0.30);
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters = { new Setter(Border.BackgroundProperty, hoverBg, "bd") }
        });
        template.Triggers.Add(new Trigger
        {
            Property = ButtonBase.IsPressedProperty,
            Value = true,
            Setters = { new Setter(Border.BackgroundProperty, pressedBg, "bd") }
        });

        return new Button
        {
            Content = text,
            Background = background,
            Foreground = foreground,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = template,
        };
    }

    private static Brush Darken(Brush brush, double amount)
    {
        if (brush is SolidColorBrush scb)
        {
            var c = scb.Color;
            byte r = (byte)Math.Max(0, c.R - (c.R * amount));
            byte g = (byte)Math.Max(0, c.G - (c.G * amount));
            byte b = (byte)Math.Max(0, c.B - (c.B * amount));
            var result = new SolidColorBrush(Color.FromArgb(c.A, r, g, b));
            result.Freeze();
            return result;
        }
        return brush;
    }

    private static string FormatPermissionBody(PermissionRequest request)
    {
        if (request.Input.ValueKind == JsonValueKind.Undefined) return "";
        try
        {
            // Show the most relevant field for common tools, like the chat step body.
            if (request.Input.TryGetProperty("command", out var cmd))
                return cmd.GetString() ?? "";
            if (request.Input.TryGetProperty("file_path", out var fp))
                return fp.GetString() ?? "";
            if (request.Input.TryGetProperty("pattern", out var pat))
                return pat.GetString() ?? "";
            var raw = request.Input.GetRawText();
            return raw.Length > 400 ? raw.Substring(0, 400) + "..." : raw;
        }
        catch
        {
            return "";
        }
    }
}
