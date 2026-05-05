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

    public static FrameworkElement BuildLoginBanner(
        string? errorMessage,
        Action onLoginClicked)
    {
        var theme = BannerTheme.Current;

        var stack = new StackPanel
        {
            Background = Brushes.Transparent,
            Margin = new Thickness(0),
        };

        stack.Children.Add(new TextBlock
        {
            Text = "Sign in to Claude",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = theme.Foreground,
            Margin = new Thickness(12, 10, 12, 4),
        });

        var detail = string.IsNullOrWhiteSpace(errorMessage)
            ? "The Claude CLI is not authenticated. Sign in to continue."
            : errorMessage!;
        stack.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = theme.Muted,
            Margin = new Thickness(12, 0, 12, 4),
            TextWrapping = TextWrapping.Wrap,
        });

        stack.Children.Add(new TextBlock
        {
            Text = "A console window will open. Complete the sign-in there, then close the window and resend your message.",
            FontSize = 11,
            Foreground = theme.Muted,
            Margin = new Thickness(12, 0, 12, 8),
            TextWrapping = TextWrapping.Wrap,
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 12, 10),
        };

        var loginBtn = MakeButton("Sign in with Claude", theme.Accent, theme.AccentForeground);
        loginBtn.Click += (_, __) => onLoginClicked();
        buttonRow.Children.Add(loginBtn);
        stack.Children.Add(buttonRow);

        return new Border
        {
            Background = theme.InputBackground,
            BorderBrush = theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(8, 8, 8, 8),
            SnapsToDevicePixels = true,
            Child = stack,
        };
    }

    public static FrameworkElement BuildQuestionCard(
        UserQuestionRequest request,
        Action<IReadOnlyDictionary<string, string>> onSubmitted)
    {
        var theme = BannerTheme.Current;

        // Build one panel per question up-front so each question's
        // selection / free-text input is preserved as the user navigates.
        var perQuestionPanel = new List<FrameworkElement>();
        var perQuestionAnswer = new List<Func<string>>();

        foreach (var q in request.Questions)
        {
            var (panel, getAnswer) = BuildSingleQuestionPanel(q, theme);
            perQuestionPanel.Add(panel);
            perQuestionAnswer.Add(getAnswer);
        }

        // Outer card: rounded Fluent container.
        var card = new Border
        {
            Background = theme.Background,
            BorderBrush = theme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(8),
            SnapsToDevicePixels = true,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.Child = root;

        // Row 0: navigation header — < > [counter] · "Claude needs more information"
        var headerGrid = new Grid { Margin = new Thickness(8, 8, 12, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var prevBtn = MakeChevronButton(isLeft: true, theme);
        Grid.SetColumn(prevBtn, 0);
        headerGrid.Children.Add(prevBtn);

        var nextBtn = MakeChevronButton(isLeft: false, theme);
        Grid.SetColumn(nextBtn, 1);
        headerGrid.Children.Add(nextBtn);

        var counterText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = theme.Muted,
        };
        Grid.SetColumn(counterText, 2);
        headerGrid.Children.Add(counterText);

        var headerLabel = new TextBlock
        {
            Text = "Claude needs more information",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Foreground = theme.Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(headerLabel, 3);
        headerGrid.Children.Add(headerLabel);

        Grid.SetRow(headerGrid, 0);
        root.Children.Add(headerGrid);

        // Row 1: thin separator under the header.
        var separator = new Border
        {
            Height = 1,
            Background = theme.Border,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.5,
        };
        Grid.SetRow(separator, 1);
        root.Children.Add(separator);

        // Row 2: current question panel host.
        var questionHost = new ContentControl
        {
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(questionHost, 2);
        root.Children.Add(questionHost);

        // Row 3: submit row.
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
                answers[request.Questions[i].Question] = perQuestionAnswer[i]();
            onSubmitted(answers);
        };
        submitRow.Children.Add(submitBtn);
        Grid.SetRow(submitRow, 3);
        root.Children.Add(submitRow);

        // Navigation state.
        int currentIndex = 0;
        void Show(int idx)
        {
            currentIndex = idx;
            questionHost.Content = perQuestionPanel[idx];
            counterText.Text = request.Questions.Count > 1
                ? $"{idx + 1} / {request.Questions.Count}"
                : "";
            var header = request.Questions[idx].Header;
            headerLabel.Text = string.IsNullOrEmpty(header)
                ? "Claude needs more information"
                : header;
            prevBtn.IsEnabled = idx > 0;
            nextBtn.IsEnabled = idx < request.Questions.Count - 1;
        }
        prevBtn.Click += (_, __) => { if (currentIndex > 0) Show(currentIndex - 1); };
        nextBtn.Click += (_, __) => { if (currentIndex < request.Questions.Count - 1) Show(currentIndex + 1); };

        // Hide nav entirely when there's a single question — chevrons are noise.
        if (request.Questions.Count <= 1)
        {
            prevBtn.Visibility = Visibility.Collapsed;
            nextBtn.Visibility = Visibility.Collapsed;
        }

        Show(0);
        return card;
    }

    private static (FrameworkElement panel, Func<string> getAnswer) BuildSingleQuestionPanel(
        UserQuestion q, BannerTheme theme)
    {
        var panel = new StackPanel { Margin = new Thickness(12, 4, 12, 4) };

        panel.Children.Add(new TextBlock
        {
            Text = q.Question,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = theme.Foreground,
            Margin = new Thickness(0, 2, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        });

        var optionsPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 4) };
        var radios = new List<RadioButton>();
        var checks = new List<CheckBox>();
        var groupName = "g_" + Guid.NewGuid().ToString("N");

        foreach (var opt in q.Options)
        {
            if (q.MultiSelect)
            {
                var cb = new CheckBox
                {
                    Content = MakeOptionContent(opt.Label, opt.Description, theme),
                    Foreground = theme.Foreground,
                    Margin = new Thickness(0, 3, 0, 3),
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
                    Margin = new Thickness(0, 3, 0, 3),
                };
                radios.Add(rb);
                optionsPanel.Children.Add(rb);
            }
        }

        var otherBox = new TextBox
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(8, 5, 8, 5),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = theme.InputBackground,
            Foreground = theme.Foreground,
            BorderBrush = theme.Border,
            BorderThickness = new Thickness(1),
            ToolTip = "Type your own answer (overrides selection)",
        };
        optionsPanel.Children.Add(new TextBlock
        {
            Text = "Or type your own:",
            Foreground = theme.Muted,
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 2),
        });
        optionsPanel.Children.Add(otherBox);

        panel.Children.Add(optionsPanel);

        Func<string> getAnswer = () =>
        {
            var typed = otherBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(typed)) return typed;
            if (q.MultiSelect)
            {
                var picked = checks
                    .Select((c, i) => (c.IsChecked == true, i))
                    .Where(t => t.Item1)
                    .Select(t => q.Options[t.i].Label);
                return string.Join(", ", picked);
            }
            var idx = radios.FindIndex(r => r.IsChecked == true);
            return idx >= 0 && idx < q.Options.Count ? q.Options[idx].Label : "";
        };

        return (panel, getAnswer);
    }

    private static Button MakeChevronButton(bool isLeft, BannerTheme theme)
    {
        // Triangle glyphs: ◀ (U+25C0) / ▶ (U+25B6). Rendered as a borderless
        // 22×22 fluent-style button that rounds on hover.
        var glyph = new TextBlock
        {
            Text = isLeft ? "◀" : "▶",
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = theme.Foreground,
        };

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "bd";
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hoverBg = WithAlpha(theme.Foreground, 0.15);
        var pressedBg = WithAlpha(theme.Foreground, 0.25);
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
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters = { new Setter(UIElement.OpacityProperty, 0.35) }
        });

        return new Button
        {
            Content = glyph,
            Width = 24,
            Height = 24,
            Margin = new Thickness(2, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = template,
            Focusable = false,
            ToolTip = isLeft ? "Previous question" : "Next question",
        };
    }

    private static Brush WithAlpha(Brush brush, double alpha)
    {
        if (brush is SolidColorBrush scb)
        {
            var c = scb.Color;
            var result = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
            result.Freeze();
            return result;
        }
        return brush;
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
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
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
