using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Text;

namespace VsAgentic.Services.Dictation;

/// <summary>
/// Recognition by the engine Windows has installed (System.Speech). It runs on
/// this computer: no key, no network. It knows only the languages added under
/// Settings → Time &amp; language → Speech, and a fresh Windows has English alone.
/// The text comes without capitals or punctuation.
/// </summary>
internal sealed class WindowsSpeechTranscriber : ISpeechToText
{
    // A recording is at most a minute; this only stops a loop that never meets the end of the stream.
    private const int MaxPhrases = 500;

    public Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken cancellationToken)
        => Task.Run(() => Recognize(wav, language, cancellationToken), cancellationToken);

    private static string Recognize(byte[] wav, string language, CancellationToken ct)
    {
        var recognizer = Pick(language);

        try
        {
            using var engine = new SpeechRecognitionEngine(recognizer);
            engine.LoadGrammar(new DictationGrammar());
            using var stream = new MemoryStream(wav);
            engine.SetInputToWaveStream(stream);

            var text = new StringBuilder();
            for (var i = 0; i < MaxPhrases; i++)
            {
                ct.ThrowIfCancellationRequested();
                // Null once the stream has ended.
                var result = engine.Recognize();
                if (result is null) break;
                if (text.Length > 0) text.Append(' ');
                text.Append(result.Text);
            }
            return text.ToString().Trim();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            throw new SpeechToTextException("The Windows speech recognizer failed: " + ex.Message, ex);
        }
    }

    /// <summary>The recognizer for the language; with no language set, the one for the language Windows is shown in.</summary>
    private static RecognizerInfo Pick(string language)
    {
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var code = (language ?? "").Split('-')[0].Trim();
        if (code.Length == 0) code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        // An exact locale ("en-GB") wins over another dialect of the same language.
        RecognizerInfo? sameLanguage = null;
        foreach (var info in installed)
        {
            if (!string.Equals(info.Culture.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(info.Culture.Name, language, StringComparison.OrdinalIgnoreCase)) return info;
            sameLanguage ??= info;
        }
        if (sameLanguage is not null) return sameLanguage;

        var have = installed.Count == 0
            ? "none"
            : string.Join(", ", installed.Select(i => i.Culture.Name));
        throw new SpeechToTextException(
            $"Windows has no speech recognizer for '{code}'. Installed: {have}. " +
            "Add the language under Settings > Time & language > Speech, or choose another provider.");
    }
}
