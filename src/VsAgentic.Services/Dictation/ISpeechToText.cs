namespace VsAgentic.Services.Dictation;

/// <summary>
/// Turns a recording into text. One implementation per provider; none of them
/// knows about the Claude CLI.
/// </summary>
public interface ISpeechToText
{
    /// <param name="wav">A complete WAV file (16 kHz, mono, 16-bit PCM).</param>
    /// <param name="language">BCP-47 code such as "sl" or "en-US", or empty for automatic detection.</param>
    Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken cancellationToken);
}

public enum SpeechProvider
{
    None,
    OpenAI,
    AzureSpeech,
    /// <summary>Any server that speaks the OpenAI transcription API (whisper.cpp, faster-whisper, LocalAI, Groq).</summary>
    OpenAICompatible,
    /// <summary>The recognizer installed in Windows (System.Speech). Runs on this computer; no key, no network.</summary>
    WindowsSpeech,
}

/// <summary>Everything a provider needs. A plain snapshot, so it does not depend on the Options page.</summary>
public sealed class SpeechSettings
{
    public SpeechProvider Provider { get; set; }
    public string ApiKey { get; set; } = "";
    /// <summary>Model name (OpenAI and compatible) — empty picks the provider's default.</summary>
    public string Model { get; set; } = "";
    /// <summary>Base URL for the compatible provider, e.g. http://localhost:8080/v1.</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>Azure region, e.g. westeurope.</summary>
    public string Region { get; set; } = "";
    public string Language { get; set; } = "";
    /// <summary>
    /// Text that tells the OpenAI models what the speech looks like: language,
    /// script, spelling of names. Empty uses a built-in sample for Slovenian and nothing for other languages.
    /// </summary>
    public string Prompt { get; set; } = "";
}

public sealed class SpeechToTextException : Exception
{
    public SpeechToTextException(string message, Exception? inner = null) : base(message, inner) { }
}
