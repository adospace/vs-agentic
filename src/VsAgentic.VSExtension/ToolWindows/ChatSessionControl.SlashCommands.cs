using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.VSExtension.ToolWindows;

// The menu under "/": the commands and skills the CLI reported, opened by a
// slash at the very start of the input (or the "/" button) and filtered by
// what follows it, up to the first space. Keys and clicks work as in the @
// list: Up/Down move, Enter or Tab picks, Escape closes.
public partial class ChatSessionControl
{
    // Set by Escape, so further typing in the same command does not bring the
    // menu straight back. Cleared once the input no longer starts with "/".
    private bool _slashDismissed;

    private void SlashCommandButton_Click(object sender, RoutedEventArgs e)
    {
        InputTextBox.Focus();
        var text = InputTextBox.Text ?? "";

        if (!text.StartsWith("/", StringComparison.Ordinal))
        {
            _suppressTextChanged = true;
            try
            {
                InputTextBox.Text = "/" + text;
                InputTextBox.CaretIndex = 1;
            }
            finally
            {
                _suppressTextChanged = false;
            }
        }
        else
        {
            InputTextBox.CaretIndex = SlashTokenEnd(text);
        }

        _slashDismissed = false;
        UpdateSlashPopup();
    }

    /// <summary>Opens, refilters or closes the menu for the input as it now stands.</summary>
    private void UpdateSlashPopup()
    {
        var text = InputTextBox.Text ?? "";
        if (!text.StartsWith("/", StringComparison.Ordinal)) _slashDismissed = false;

        if (_slashDismissed || !TryGetSlashToken(text, InputTextBox.CaretIndex, out var typed))
        {
            CloseSlashPopup();
            return;
        }
        ShowSlashPopup(typed);
    }

    // What was typed after the slash, while the caret is still inside the
    // command's name.
    private static bool TryGetSlashToken(string text, int caret, out string typed)
    {
        typed = "";
        if (text.Length == 0 || text[0] != '/' || caret < 1 || caret > text.Length) return false;

        var token = text.Substring(1, caret - 1);
        if (token.Any(char.IsWhiteSpace)) return false;

        typed = token;
        return true;
    }

    // The end of the command's name: the first whitespace, or the end of the text.
    private static int SlashTokenEnd(string text)
    {
        for (var i = 1; i < text.Length; i++)
            if (char.IsWhiteSpace(text[i])) return i;
        return text.Length;
    }

    private void ShowSlashPopup(string typed)
    {
        if (DataContext is not ChatSessionViewModel vm)
        {
            CloseSlashPopup();
            return;
        }

        if (vm.SlashCommands.Count == 0)
        {
            // Nothing reported yet. Said once, for the bare slash only, so a
            // message that merely starts with "/" is not covered by a note.
            SlashList.ItemsSource = null;
            SlashList.Visibility = Visibility.Collapsed;
            SlashHint.Visibility = Visibility.Visible;
            SlashPopup.IsOpen = typed.Length == 0;
            return;
        }

        var results = vm.FilterSlashCommands(typed);
        if (results.Count == 0)
        {
            CloseSlashPopup();
            return;
        }

        var view = new ListCollectionView(results.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SlashCommandOption.Group)));
        SlashList.ItemsSource = view;
        SlashList.SelectedIndex = 0;
        SlashList.Visibility = Visibility.Visible;
        SlashHint.Visibility = Visibility.Collapsed;
        SlashPopup.IsOpen = true;
    }

    private void CloseSlashPopup()
    {
        SlashPopup.IsOpen = false;
        SlashList.ItemsSource = null;
    }

    /// <summary>The menu's keys. True when the key was used.</summary>
    private bool HandleSlashKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSlashSelection(+1);
                return true;
            case Key.Up:
                MoveSlashSelection(-1);
                return true;
            case Key.Enter:
            case Key.Tab:
                // Ctrl+Enter still sends what is typed.
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) return false;
                if (SlashList.SelectedItem is not SlashCommandOption option) return false;
                CommitSlashCommand(option);
                return true;
            case Key.Escape:
                _slashDismissed = true;
                CloseSlashPopup();
                return true;
        }
        return false;
    }

    private void MoveSlashSelection(int delta)
    {
        var count = SlashList.Items.Count;
        if (count == 0) return;

        var idx = SlashList.SelectedIndex;
        if (idx < 0) idx = delta > 0 ? -1 : 0;
        SlashList.SelectedIndex = ((idx + delta) % count + count) % count;
        if (SlashList.SelectedItem is { } sel)
            SlashList.ScrollIntoView(sel);
    }

    private void SlashList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!SlashPopup.IsOpen) return;
        // On preview, as in the @ list, so the click picks before the list
        // item takes focus from the input box.
        if (e.OriginalSource is DependencyObject src
            && FindAncestor<ListBoxItem>(src)?.DataContext is SlashCommandOption option)
        {
            CommitSlashCommand(option);
            InputTextBox.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Writes "/name " in place of what was typed, ready for arguments. A
    /// command the model panel covers opens the panel instead, when its
    /// button is on screen.
    /// </summary>
    private void CommitSlashCommand(SlashCommandOption option)
    {
        var text = InputTextBox.Text ?? "";
        var end = SlashTokenEnd(text);
        var rest = text.Substring(end).TrimStart();

        if (option.OpensModelPanel && ModelPickerToggle.IsVisible)
        {
            SetInputText(rest, 0);
            CloseSlashPopup();
            ModelPickerToggle.IsChecked = true;
            return;
        }

        var command = "/" + option.Name + " ";
        SetInputText(command + rest, command.Length);
        CloseSlashPopup();
    }

    private void SetInputText(string text, int caret)
    {
        _suppressTextChanged = true;
        try
        {
            InputTextBox.Text = text;
            InputTextBox.CaretIndex = caret;
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }
}
