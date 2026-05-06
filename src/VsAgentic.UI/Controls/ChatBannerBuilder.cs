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

        var root = new StackPanel { Margin = new Thickness(0) };
        SetBrush(root, StackPanel.BackgroundProperty, BannerThemeKeys.Background, theme.Background);

        var header = new TextBlock
        {
            Text = $"Claude wants to use {request.ToolName}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(12, 10, 12, 4),
        };
        SetBrush(header, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        root.Children.Add(header);

        var bodyText = FormatPermissionBody(request);
        if (!string.IsNullOrEmpty(bodyText))
        {
            var body = new TextBlock
            {
                Text = bodyText,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo, monospace"),
                FontSize = 12,
                Margin = new Thickness(12, 0, 12, 8),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 160,
            };
            SetBrush(body, TextBlock.ForegroundProperty, BannerThemeKeys.Muted, theme.Muted);
            root.Children.Add(body);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 12, 10),
        };

        var allowBtn = MakeButton("Allow",
            BannerThemeKeys.Accent, theme.Accent,
            BannerThemeKeys.AccentForeground, theme.AccentForeground);
        allowBtn.Click += (_, __) =>
        {
            var inputJson = request.Input.ValueKind == JsonValueKind.Undefined ? "{}" : request.Input.GetRawText();
            onResolved(PermissionDecision.Allow(inputJson));
        };
        // Deny stays on the static danger brushes; VS doesn't expose a
        // semantic "destructive" key worth swapping in.
        var denyBtn = MakeButton("Deny",
            null, theme.Danger,
            null, theme.DangerForeground);
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

        var title = new TextBlock
        {
            Text = "Sign in to Claude",
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(12, 10, 12, 4),
        };
        SetBrush(title, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        stack.Children.Add(title);

        var detail = string.IsNullOrWhiteSpace(errorMessage)
            ? "The Claude CLI is not authenticated. Sign in to continue."
            : errorMessage!;
        var detailBlock = new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Margin = new Thickness(12, 0, 12, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        SetBrush(detailBlock, TextBlock.ForegroundProperty, BannerThemeKeys.Muted, theme.Muted);
        stack.Children.Add(detailBlock);

        var subText = new TextBlock
        {
            Text = "A console window will open. Complete the sign-in there, then close the window and resend your message.",
            FontSize = 11,
            Margin = new Thickness(12, 0, 12, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        SetBrush(subText, TextBlock.ForegroundProperty, BannerThemeKeys.Muted, theme.Muted);
        stack.Children.Add(subText);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(8, 0, 12, 10),
        };

        var loginBtn = MakeButton("Sign in with Claude",
            BannerThemeKeys.Accent, theme.Accent,
            BannerThemeKeys.AccentForeground, theme.AccentForeground);
        loginBtn.Click += (_, __) => onLoginClicked();
        buttonRow.Children.Add(loginBtn);
        stack.Children.Add(buttonRow);

        var outer = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(8, 8, 8, 8),
            SnapsToDevicePixels = true,
            Child = stack,
        };
        SetBrush(outer, Border.BackgroundProperty, BannerThemeKeys.InputBackground, theme.InputBackground);
        SetBrush(outer, Border.BorderBrushProperty, BannerThemeKeys.Border, theme.Border);
        return outer;
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
        var perQuestionIsAnswered = new List<Func<bool>>();

        foreach (var q in request.Questions)
        {
            var (panel, getAnswer, isAnswered) = BuildSingleQuestionPanel(q, theme);
            perQuestionPanel.Add(panel);
            perQuestionAnswer.Add(getAnswer);
            perQuestionIsAnswered.Add(isAnswered);
        }

        // Outer card: rounded Fluent container. Both background and border
        // prefer a DynamicResource bound to a VS theme key when the host
        // has registered one (e.g. VsBrushes.ComboBoxBackgroundKey). VS
        // updates the brush behind that key in place when the theme
        // changes, so the card re-tints automatically without a rebuild.
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(8),
            SnapsToDevicePixels = true,
        };
        SetBrush(card, Border.BackgroundProperty, BannerThemeKeys.Background, theme.Background);
        SetBrush(card, Border.BorderBrushProperty, BannerThemeKeys.Border, theme.Border);

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
        };
        SetBrush(counterText, TextBlock.ForegroundProperty, BannerThemeKeys.Muted, theme.Muted);
        Grid.SetColumn(counterText, 2);
        headerGrid.Children.Add(counterText);

        // Opacity (not a derived alpha brush) so the live themed brush keeps
        // tracking when the IDE theme changes.
        var headerLabel = new TextBlock
        {
            Text = "Claude needs more information",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontSize = 12,
            Opacity = 0.85,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        SetBrush(headerLabel, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        Grid.SetColumn(headerLabel, 3);
        headerGrid.Children.Add(headerLabel);

        Grid.SetRow(headerGrid, 0);
        root.Children.Add(headerGrid);

        // Row 1: thin separator under the header.
        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.5,
        };
        SetBrush(separator, Border.BackgroundProperty, BannerThemeKeys.Border, theme.Border);
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
        var submitBtn = MakeButton("Submit",
            BannerThemeKeys.Accent, theme.Accent,
            BannerThemeKeys.AccentForeground, theme.AccentForeground);
        submitRow.Children.Add(submitBtn);
        Grid.SetRow(submitRow, 3);
        root.Children.Add(submitRow);

        // Navigation state.
        int currentIndex = 0;
        int FindFirstUnanswered(int startAfter = -1)
        {
            for (int i = startAfter + 1; i < perQuestionIsAnswered.Count; i++)
                if (!perQuestionIsAnswered[i]()) return i;
            // Wrap from the beginning so we can detect any earlier gaps too.
            for (int i = 0; i <= startAfter && i < perQuestionIsAnswered.Count; i++)
                if (!perQuestionIsAnswered[i]()) return i;
            return -1;
        }
        void RefreshSubmitLabel()
        {
            bool allAnswered = FindFirstUnanswered() < 0;
            submitBtn.Content = allAnswered ? "Submit" : "Next unanswered";
        }
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
            RefreshSubmitLabel();
        }
        prevBtn.Click += (_, __) => { if (currentIndex > 0) Show(currentIndex - 1); };
        nextBtn.Click += (_, __) => { if (currentIndex < request.Questions.Count - 1) Show(currentIndex + 1); };

        submitBtn.Click += (_, __) =>
        {
            // If anything is still blank, jump to the next blank question
            // (starting after the current one) instead of submitting.
            var unanswered = FindFirstUnanswered(currentIndex);
            if (unanswered >= 0)
            {
                Show(unanswered);
                return;
            }

            var answers = new Dictionary<string, string>();
            for (int i = 0; i < request.Questions.Count; i++)
                answers[request.Questions[i].Question] = perQuestionAnswer[i]();
            onSubmitted(answers);
        };

        // Hide nav entirely when there's a single question — chevrons are noise.
        if (request.Questions.Count <= 1)
        {
            prevBtn.Visibility = Visibility.Collapsed;
            nextBtn.Visibility = Visibility.Collapsed;
        }

        Show(0);
        return card;
    }

    private static (FrameworkElement panel, Func<string> getAnswer, Func<bool> isAnswered) BuildSingleQuestionPanel(
        UserQuestion q, BannerTheme theme)
    {
        var panel = new StackPanel { Margin = new Thickness(12, 6, 12, 6) };

        var questionText = new TextBlock
        {
            Text = q.Question,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 2, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        SetBrush(questionText, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        panel.Children.Add(questionText);

        var optionsPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 4) };
        var radios = new List<RadioButton>();
        var checks = new List<CheckBox>();
        var groupName = "g_" + Guid.NewGuid().ToString("N");

        foreach (var opt in q.Options)
        {
            if (q.MultiSelect)
            {
                var cb = MakeFluentCheckBox(theme);
                cb.Content = MakeOptionContent(opt.Label, opt.Description, theme);
                cb.Margin = new Thickness(0, 3, 0, 3);
                checks.Add(cb);
                optionsPanel.Children.Add(cb);
            }
            else
            {
                var rb = MakeFluentRadioButton(theme, groupName);
                rb.Content = MakeOptionContent(opt.Label, opt.Description, theme);
                rb.Margin = new Thickness(0, 3, 0, 3);
                radios.Add(rb);
                optionsPanel.Children.Add(rb);
            }
        }

        // "Other" row: a radio (single-select) or checkbox (multi-select) sits
        // on the same line as the free-text TextBox so the user can keep their
        // selection while still entering a custom value.
        var otherBox = new TextBox
        {
            Padding = new Thickness(8, 5, 8, 5),
            MinWidth = 200,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(1),
            ToolTip = "Type your own answer",
        };
        SetBrush(otherBox, TextBox.BackgroundProperty, BannerThemeKeys.InputBackground, theme.InputBackground);
        SetBrush(otherBox, TextBox.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        SetBrush(otherBox, TextBox.BorderBrushProperty, BannerThemeKeys.Border, theme.Border);

        var otherRow = new Grid { Margin = new Thickness(0, 6, 0, 2) };
        otherRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        otherRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        RadioButton? otherRadio = null;
        CheckBox? otherCheck = null;

        var otherLabel = new TextBlock
        {
            Text = "Other:",
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetBrush(otherLabel, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);

        if (q.MultiSelect)
        {
            otherCheck = MakeFluentCheckBox(theme);
            otherCheck.Content = otherLabel;
            otherCheck.Margin = new Thickness(0, 0, 8, 0);
            otherCheck.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(otherCheck, 0);
            otherRow.Children.Add(otherCheck);
        }
        else
        {
            otherRadio = MakeFluentRadioButton(theme, groupName);
            otherRadio.Content = otherLabel;
            otherRadio.Margin = new Thickness(0, 0, 8, 0);
            otherRadio.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(otherRadio, 0);
            otherRow.Children.Add(otherRadio);
        }

        Grid.SetColumn(otherBox, 1);
        otherRow.Children.Add(otherBox);
        optionsPanel.Children.Add(otherRow);

        // Auto-tick the Other radio/checkbox when the user focuses or types in
        // the free-text box, so the value is actually counted as the answer.
        void TickOther()
        {
            if (otherRadio != null && otherRadio.IsChecked != true) otherRadio.IsChecked = true;
            if (otherCheck != null && otherCheck.IsChecked != true) otherCheck.IsChecked = true;
        }
        otherBox.GotKeyboardFocus += (_, __) => TickOther();
        otherBox.TextChanged += (_, __) =>
        {
            if (!string.IsNullOrEmpty(otherBox.Text)) TickOther();
        };
        // Conversely, when the user clicks the radio/checkbox, focus the
        // textbox so they can type immediately.
        if (otherRadio != null)
            otherRadio.Checked += (_, __) => { if (!otherBox.IsKeyboardFocused) otherBox.Focus(); };
        if (otherCheck != null)
            otherCheck.Checked += (_, __) => { if (!otherBox.IsKeyboardFocused) otherBox.Focus(); };

        panel.Children.Add(optionsPanel);

        bool OtherIsActive()
        {
            var typed = (otherBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(typed)) return false;
            if (otherRadio != null) return otherRadio.IsChecked == true;
            if (otherCheck != null) return otherCheck.IsChecked == true;
            return false;
        }

        Func<string> getAnswer = () =>
        {
            var typed = (otherBox.Text ?? "").Trim();
            if (q.MultiSelect)
            {
                var picked = checks
                    .Select((c, i) => (c.IsChecked == true, i))
                    .Where(t => t.Item1)
                    .Select(t => q.Options[t.i].Label)
                    .ToList();
                if (OtherIsActive()) picked.Add(typed);
                return string.Join(", ", picked);
            }
            if (OtherIsActive()) return typed;
            var idx = radios.FindIndex(r => r.IsChecked == true);
            return idx >= 0 && idx < q.Options.Count ? q.Options[idx].Label : "";
        };

        Func<bool> isAnswered = () =>
        {
            if (OtherIsActive()) return true;
            if (q.MultiSelect) return checks.Any(c => c.IsChecked == true);
            return radios.Any(r => r.IsChecked == true);
        };

        return (panel, getAnswer, isAnswered);
    }

    private static Button MakeChevronButton(bool isLeft, BannerTheme theme)
    {
        // Chevron glyphs from the Windows icon font (Segoe MDL2 Assets on
        // Win10, Segoe Fluent Icons on Win11 — both expose the same code
        // points). U+E76B = ChevronLeft, U+E76C = ChevronRight.
        var glyph = new TextBlock
        {
            Text = isLeft ? "" : "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        SetBrush(glyph, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);

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

    private static CheckBox MakeFluentCheckBox(BannerTheme theme)
    {
        var cb = new CheckBox
        {
            VerticalContentAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = BuildToggleTemplate(typeof(CheckBox), theme, isRadio: false),
        };
        SetBrush(cb, Control.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        return cb;
    }

    private static RadioButton MakeFluentRadioButton(BannerTheme theme, string groupName)
    {
        var rb = new RadioButton
        {
            GroupName = groupName,
            VerticalContentAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = BuildToggleTemplate(typeof(RadioButton), theme, isRadio: true),
        };
        SetBrush(rb, Control.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        return rb;
    }

    private static ControlTemplate BuildToggleTemplate(Type targetType, BannerTheme theme, bool isRadio)
    {
        // Horizontal layout: indicator on the left, label/content on the right.
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        stack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        // The indicator: 16x16 rounded box for CheckBox, circle for RadioButton.
        var box = new FrameworkElementFactory(typeof(Border));
        box.Name = "box";
        box.SetValue(Border.WidthProperty, 16.0);
        box.SetValue(Border.HeightProperty, 16.0);
        box.SetValue(Border.CornerRadiusProperty, new CornerRadius(isRadio ? 8 : 4));
        SetBrush(box, Border.BorderBrushProperty, BannerThemeKeys.IndicatorBorder, theme.Border);
        box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        box.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        box.SetValue(Border.MarginProperty, new Thickness(0, 1, 8, 1));
        box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        box.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        // Glyph: filled dot for radio, check mark for checkbox. Glyph fill is
        // the AccentForeground (rendered on top of the Accent-filled box).
        FrameworkElementFactory glyph;
        if (isRadio)
        {
            // 6×6 dot in a 14×14 inner area centers cleanly at 4px each side
            // (integer offset). 7×7 sub-pixel-snaps to a 0.5px lean.
            glyph = new FrameworkElementFactory(typeof(Border));
            glyph.SetValue(Border.WidthProperty, 6.0);
            glyph.SetValue(Border.HeightProperty, 6.0);
            glyph.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            SetBrush(glyph, Border.BackgroundProperty, BannerThemeKeys.AccentForeground, theme.AccentForeground);
        }
        else
        {
            glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetValue(TextBlock.TextProperty, "✓"); // ✓
            glyph.SetValue(TextBlock.FontSizeProperty, 12.0);
            glyph.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            SetBrush(glyph, TextBlock.ForegroundProperty, BannerThemeKeys.AccentForeground, theme.AccentForeground);
            glyph.SetValue(TextBlock.LineHeightProperty, 12.0);
            glyph.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        }
        glyph.Name = "glyph";
        glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        box.AppendChild(glyph);
        stack.AppendChild(box);

        // Label / content presenter on the right.
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        stack.AppendChild(content);

        var template = new ControlTemplate(targetType) { VisualTree = stack };

        // Hover: brighten the indicator border so the user feels the
        // affordance. BrushValue feeds the trigger's Setter a
        // DynamicResourceExtension when a key is registered, or the
        // static fallback brush otherwise.
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true,
            Setters =
            {
                new Setter(Border.BorderBrushProperty,
                    BrushValue(BannerThemeKeys.Foreground, theme.Foreground), "box"),
            },
        });

        // Checked: fill the indicator with Accent and reveal the glyph.
        template.Triggers.Add(new Trigger
        {
            Property = ToggleButton.IsCheckedProperty,
            Value = true,
            Setters =
            {
                new Setter(Border.BackgroundProperty,
                    BrushValue(BannerThemeKeys.Accent, theme.Accent), "box"),
                new Setter(Border.BorderBrushProperty,
                    BrushValue(BannerThemeKeys.Accent, theme.Accent), "box"),
                new Setter(UIElement.VisibilityProperty, Visibility.Visible, "glyph"),
            },
        });

        // Disabled: dim the whole control.
        template.Triggers.Add(new Trigger
        {
            Property = UIElement.IsEnabledProperty,
            Value = false,
            Setters = { new Setter(UIElement.OpacityProperty, 0.5) },
        });

        return template;
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

    // -- DynamicResource bridge helpers ------------------------------------
    //
    // When BannerThemeKeys.X is non-null (the host has registered a theme
    // key — typically VsBrushes.X in the VS extension), the brush property
    // is wired with SetResourceReference so the live VS-themed brush is
    // consulted and tracks IDE theme changes without a rebuild. When the
    // key is null (Desktop, no VS theme system) the static fallback brush
    // from BannerTheme.Current is assigned directly.

    private static void SetBrush(FrameworkElement el, DependencyProperty prop, object? key, Brush fallback)
    {
        if (key != null)
            el.SetResourceReference(prop, key);
        else
            el.SetValue(prop, fallback);
    }

    private static void SetBrush(FrameworkElementFactory factory, DependencyProperty prop, object? key, Brush fallback)
    {
        if (key != null)
            factory.SetResourceReference(prop, key);
        else
            factory.SetValue(prop, fallback);
    }

    // Setter value for ControlTemplate triggers. DynamicResourceExtension
    // is the code equivalent of XAML's {DynamicResource X}; WPF resolves
    // the extension when the setter is applied to the target element.
    private static object BrushValue(object? key, Brush fallback)
    {
        return key != null ? (object)new DynamicResourceExtension(key) : fallback;
    }

    private static FrameworkElement MakeOptionContent(string label, string description, BannerTheme theme)
    {
        var sp = new StackPanel { Orientation = Orientation.Vertical };

        var labelBlock = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
        };
        SetBrush(labelBlock, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
        sp.Children.Add(labelBlock);

        if (!string.IsNullOrEmpty(description))
        {
            // Sub-label / hint: 78% of the theme Foreground reads better in
            // both light and dark themes than the dim Muted brush. Opacity
            // (not a derived alpha brush) keeps the live themed brush ref
            // intact so theme changes propagate.
            var descBlock = new TextBlock
            {
                Text = description,
                Opacity = 0.78,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            };
            SetBrush(descBlock, TextBlock.ForegroundProperty, BannerThemeKeys.Foreground, theme.Foreground);
            sp.Children.Add(descBlock);
        }
        return sp;
    }

    private static Button MakeButton(
        string text,
        object? bgKey, Brush bgFallback,
        object? fgKey, Brush fgFallback)
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

        // Hover/pressed shades stay derived from the static fallback. They
        // are momentary states; if the user happens to hover during a
        // theme switch the visual mismatches for a fraction of a second.
        var hoverBg = Darken(bgFallback, 0.15);
        var pressedBg = Darken(bgFallback, 0.30);
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

        var btn = new Button
        {
            Content = text,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = template,
        };
        SetBrush(btn, Control.BackgroundProperty, bgKey, bgFallback);
        SetBrush(btn, Control.ForegroundProperty, fgKey, fgFallback);
        return btn;
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
