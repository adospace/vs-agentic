using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using VsAgentic.Services.Dictation;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// Tools → Options → VsAgentic → Dictation. The API key is the one setting that
/// does not go to the registry: it lives in Windows Credential Manager.
/// </summary>
[ComVisible(true)]
[Guid("5d0f6a52-8f0b-4a47-9f6e-3d7b1c2a9e41")]
public class DictationOptionsPage : DialogPage
{
    private const string CredentialTarget = "VsAgentic/Dictation";

    private string? _apiKey;

    [Category("Speech recognition")]
    [DisplayName("Provider")]
    [Description("Who turns your voice into text. None (default): dictation is off, and the microphone button opens this page. The recording is sent to the provider you choose, and to no one else. OpenAI and Azure Speech need a key; OpenAICompatible is for a server of your own (whisper.cpp, faster-whisper, LocalAI) or another service that speaks the OpenAI transcription API.")]
    [DefaultValue(SpeechProvider.None)]
    public SpeechProvider Provider { get; set; } = SpeechProvider.None;

    [Category("Speech recognition settings")]
    [DisplayName("API key")]
    [Description("Key for the provider. Stored in Windows Credential Manager under your account, not in the Visual Studio settings. A local server that needs no key can leave it empty.")]
    [PasswordPropertyText(true)]
    public string ApiKey
    {
        get => _apiKey ??= CredentialStore.Read(CredentialTarget);
        set => _apiKey = value ?? "";
    }

    [Category("Speech recognition settings")]
    [DisplayName("Model")]
    [Description("OpenAI and OpenAICompatible only. Empty uses gpt-4o-transcribe for OpenAI and whisper-1 for a compatible server. Other OpenAI choices: gpt-4o-mini-transcribe (cheaper), whisper-1.")]
    [DefaultValue("")]
    public string Model { get; set; } = "";

    [Category("Speech recognition settings")]
    [DisplayName("Endpoint")]
    [Description("OpenAICompatible only. Base URL up to and including the version, for example http://localhost:8080/v1. The extension adds /audio/transcriptions.")]
    [DefaultValue("")]
    public string Endpoint { get; set; } = "";

    [Category("Speech recognition settings")]
    [DisplayName("Azure region")]
    [Description("AzureSpeech only. The region of the Speech resource, for example westeurope.")]
    [DefaultValue("")]
    public string Region { get; set; } = "";

    [Category("Speech recognition settings")]
    [DisplayName("Language")]
    [Description("What you speak, as a language code such as sl, en or de. Empty detects it automatically, except with AzureSpeech, which then assumes en-US.")]
    [DefaultValue("")]
    public string Language { get; set; } = "";

    public SpeechSettings ToSettings() => new()
    {
        Provider = Provider,
        ApiKey = (ApiKey ?? "").Trim(),
        Model = (Model ?? "").Trim(),
        Endpoint = (Endpoint ?? "").Trim(),
        Region = (Region ?? "").Trim(),
        Language = (Language ?? "").Trim(),
    };

    // The registry gets every property except the key.
    protected override void SaveSetting(PropertyDescriptor property)
    {
        if (property.Name == nameof(ApiKey)) return;
        base.SaveSetting(property);
    }

    protected override void LoadSettingFromStorage(PropertyDescriptor property)
    {
        if (property.Name == nameof(ApiKey)) return;
        base.LoadSettingFromStorage(property);
    }

    public override void SaveSettingsToStorage()
    {
        base.SaveSettingsToStorage();
        try { CredentialStore.Write(CredentialTarget, (_apiKey ?? "").Trim()); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VsAgentic: Failed to store the speech key: {ex}");
        }
    }

    public override void LoadSettingsFromStorage()
    {
        base.LoadSettingsFromStorage();
        _apiKey = null;
    }
}
