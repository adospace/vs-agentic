using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VsAgentic.Services.Dictation;

public static class SpeechToTextFactory
{
    // One client for all requests, as HttpClient intends. Recordings are short;
    // the timeout only stops a dead server from holding the button forever.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Returns null while no provider is chosen or its required fields are empty.</summary>
    public static ISpeechToText? Create(SpeechSettings s) => s.Provider switch
    {
        SpeechProvider.OpenAI when s.ApiKey.Length > 0
            => new OpenAiTranscriber(Http, "https://api.openai.com/v1", s.ApiKey,
                s.Model.Length > 0 ? s.Model : "gpt-4o-transcribe"),
        SpeechProvider.OpenAICompatible when s.Endpoint.Length > 0
            => new OpenAiTranscriber(Http, s.Endpoint, s.ApiKey,
                s.Model.Length > 0 ? s.Model : "whisper-1"),
        SpeechProvider.AzureSpeech when s.ApiKey.Length > 0 && s.Region.Length > 0
            => new AzureTranscriber(Http, s.Region, s.ApiKey),
        _ => null,
    };

    /// <summary>The first line of a failed response, so the user sees why instead of a status code.</summary>
    internal static async Task<string> DescribeFailureAsync(HttpResponseMessage response)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
        catch { body = ""; }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
                if (err.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            }
        }
        catch (JsonException) { }

        var line = body.Trim();
        var cut = line.IndexOfAny(new[] { '\r', '\n' });
        if (cut >= 0) line = line.Substring(0, cut);
        if (line.Length > 200) line = line.Substring(0, 200);
        return line.Length > 0 ? line : response.ReasonPhrase ?? "";
    }
}

internal sealed class OpenAiTranscriber : ISpeechToText
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _apiKey;
    private readonly string _model;

    public OpenAiTranscriber(HttpClient http, string baseUrl, string apiKey, string model)
    {
        _http = http;
        _url = baseUrl.TrimEnd('/') + "/audio/transcriptions";
        _apiKey = apiKey;
        _model = model;
    }

    public async Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "dictation.wav");
        form.Add(new StringContent(_model, Encoding.UTF8), "model");
        form.Add(new StringContent("json", Encoding.UTF8), "response_format");

        // The API wants an ISO-639-1 code: "sl", not "sl-SI".
        var code = language.Split('-')[0].Trim().ToLowerInvariant();
        if (code.Length > 0)
            form.Add(new StringContent(code, Encoding.UTF8), "language");

        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = form };
        if (_apiKey.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SpeechToTextException(
                    $"The speech service answered {(int)response.StatusCode}: {await SpeechToTextFactory.DescribeFailureAsync(response).ConfigureAwait(false)}");

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechToTextException("Could not reach the speech service: " + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new SpeechToTextException("The speech service sent an answer that is not JSON.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SpeechToTextException("The speech service did not answer in time.", ex);
        }
    }
}

internal sealed class AzureTranscriber : ISpeechToText
{
    private readonly HttpClient _http;
    private readonly string _region;
    private readonly string _apiKey;

    public AzureTranscriber(HttpClient http, string region, string apiKey)
    {
        _http = http;
        _region = region.Trim();
        _apiKey = apiKey;
    }

    public async Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken ct)
    {
        // Azure's short-audio REST API takes a locale and no automatic detection;
        // it also stops after 60 seconds of audio.
        var locale = language.Length == 0 ? "en-US" : ToLocale(language);
        var url = $"https://{_region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1" +
                  $"?language={Uri.EscapeDataString(locale)}&format=simple";

        using var content = new ByteArrayContent(wav);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav; codecs=audio/pcm; samplerate=16000");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("Ocp-Apim-Subscription-Key", _apiKey);

        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new SpeechToTextException(
                    $"Azure Speech answered {(int)response.StatusCode}: {await SpeechToTextFactory.DescribeFailureAsync(response).ConfigureAwait(false)}");

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var status = doc.RootElement.TryGetProperty("RecognitionStatus", out var s) ? s.GetString() : "";
            if (status == "NoMatch" || status == "InitialSilenceTimeout") return "";
            if (status != "Success")
                throw new SpeechToTextException("Azure Speech could not recognise the recording: " + status);
            return doc.RootElement.TryGetProperty("DisplayText", out var t) ? (t.GetString() ?? "").Trim() : "";
        }
        catch (HttpRequestException ex)
        {
            throw new SpeechToTextException("Could not reach Azure Speech: " + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new SpeechToTextException("Azure Speech sent an answer that is not JSON.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SpeechToTextException("Azure Speech did not answer in time.", ex);
        }
    }

    /// <summary>"sl" becomes "sl-SI"; a full locale stays as it is.</summary>
    internal static string ToLocale(string language)
    {
        language = language.Trim();
        if (language.IndexOf('-') >= 0) return language;
        switch (language.ToLowerInvariant())
        {
            case "sl": return "sl-SI";
            case "en": return "en-US";
            case "de": return "de-DE";
            case "fr": return "fr-FR";
            case "it": return "it-IT";
            case "es": return "es-ES";
            case "hr": return "hr-HR";
            default: return language.ToLowerInvariant() + "-" + language.ToUpperInvariant();
        }
    }
}
