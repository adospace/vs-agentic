using System;
using System.Collections.Generic;
using VsAgentic.UI.ViewModels;

namespace VsAgentic.VSExtension.ToolWindows;

// The buttons under the setup check's report in the chat (Install Claude
// Code, Sign in …): the page reports a click, the view model runs the fix and
// sends the next set of buttons back.
public partial class ChatSessionControl
{
    private void InitializeSetupFixes(ChatSessionViewModel viewModel)
    {
        viewModel.NoticeActionsChanged += OnNoticeActionsChanged;
        ChatWebView.NoticeActionRequested += OnNoticeActionRequested;
    }

    private void OnNoticeActionsChanged(string id, IReadOnlyList<ChatNoticeAction> actions)
    {
        if (Dispatcher.CheckAccess())
            _ = ChatWebView.SetNoticeActionsAsync(id, actions);
        else
            Dispatcher.BeginInvoke(new Action(() => _ = ChatWebView.SetNoticeActionsAsync(id, actions)));
    }

    private void OnNoticeActionRequested(string id, string action)
    {
        if (DataContext is ChatSessionViewModel vm)
            vm.OnNoticeAction(id, action);
    }
}
