using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VsAgentic.UI.ViewModels.Banners;

public partial class LoginBannerViewModel : ObservableObject, IBannerViewModel
{
    private readonly Action _onLoginClicked;

    public string Title => "Sign in to Claude";
    public string DetailMessage { get; }
    public string SubText =>
        "A console window will open. Complete the sign-in there, then close the window and resend your message.";

    public LoginBannerViewModel(string? errorMessage, Action onLoginClicked)
    {
        _onLoginClicked = onLoginClicked;
        DetailMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? "The Claude CLI is not authenticated. Sign in to continue."
            : errorMessage!;
    }

    [RelayCommand]
    private void Login() => _onLoginClicked();
}
