using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using VsAgentic.UI.ViewModels.Banners;

namespace VsAgentic.VSExtension.ToolWindows.Banners;

public partial class QuestionCardControl : UserControl
{
    public QuestionCardControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The card's keyboard shortcuts. Taken on the tunnelling event so the
    /// "Other" text box does not see the keys first, and marked handled so
    /// they do not carry on to the shell.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not QuestionCardViewModel vm) return;

        // Ctrl+Enter answers the card — the same chord that sends a message
        // from the chat input, so the two text boxes behave alike.
        if (e.Key == Key.Enter &&
            (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (!vm.SubmitCommand.CanExecute(null)) return;
            vm.SubmitCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Alt+N picks the nth row from anywhere on the card, the "Other" box
        // included. Arrow keys cannot leave a text box — they move the caret —
        // so without this the typed answer is a dead end. Alt turns the real
        // key into SystemKey.
        //
        // Focus moves with the pick: the shortcut is also how the user gets
        // out of the text box, and focus left behind would make the next arrow
        // key act on a row they are no longer looking at.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Alt && OrdinalOf(key) is int ordinal)
        {
            var question = vm.CurrentQuestion;

            if (ordinal == question.OtherOrdinal)
            {
                question.SelectOther();
                FindOtherBox(this)?.Focus();
                e.Handled = true;
                return;
            }

            if (!question.SelectOption(ordinal)) return;
            FindOptions(this).FirstOrDefault(o => Ordinal(o) == ordinal)?.Focus();
            e.Handled = true;
            return;
        }

        // Up off the first character steps out of the "Other" box, which the
        // text box would otherwise swallow, and lands on the last option. Not
        // on the "Other" toggle: that one shares the row with the box, so
        // stopping there would look like the focus had not moved at all.
        if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None &&
            Keyboard.FocusedElement is TextBox { CaretIndex: 0 } &&
            FindOptions(this).LastOrDefault() is { } last)
        {
            last.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Puts keyboard focus on the first option of the question that just came
    /// up, so the card can be answered without reaching for the mouse. Runs
    /// once per page as well: the host ContentControl rebuilds this subtree
    /// every time CurrentQuestion changes.
    /// </summary>
    private void QuestionRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement root) return;

        // Two cases are safe to take focus in: the chat window already holds
        // it, or nothing in this window does — the user is off in another
        // window, so there is nothing to take and the card is waiting for them
        // when they come back. Anything else is left alone: a question can
        // arrive while the user is typing in the editor, and a tool window
        // that pulls focus out of there is worse than one that waits to be
        // clicked.
        if (FindChat() is not { } chat) return;
        if (!chat.IsKeyboardFocusWithin && Keyboard.FocusedElement is not null) return;

        // The ItemsControl generates the option controls during the layout
        // pass, so none of them is in the tree yet while Loaded runs. This
        // subscription drops itself after the first pass.
        void FocusFirstOption(object? _, EventArgs __)
        {
            root.LayoutUpdated -= FocusFirstOption;
            FindOptions(root).FirstOrDefault()?.Focus();
        }

        root.LayoutUpdated += FocusFirstOption;
    }

    /// <summary>
    /// The option toggles under <paramref name="root"/>, in the order they are
    /// listed. Selected by data type, which leaves out the "Other" toggle:
    /// that one carries the question, not an option.
    /// </summary>
    private static IEnumerable<ToggleButton> FindOptions(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ToggleButton { DataContext: OptionViewModel } toggle)
            {
                yield return toggle;
                continue;
            }

            foreach (var nested in FindOptions(child)) yield return nested;
        }
    }

    /// <summary>The "Other" free-text box: the card's only text box.</summary>
    private static TextBox? FindOtherBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox box) return box;

            if (FindOtherBox(child) is { } nested) return nested;
        }
        return null;
    }

    private static int Ordinal(ToggleButton toggle) =>
        toggle.DataContext is OptionViewModel option ? option.Ordinal : 0;

    private static int? OrdinalOf(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
        _ => null,
    };

    private ChatSessionControl? FindChat()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is ChatSessionControl chat) return chat;

            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
