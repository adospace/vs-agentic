using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VsAgentic.Services.Dictation;
using Xunit;

namespace VsAgentic.Services.Tests;

/// <summary>
/// Which provider the settings select, and what the OpenAI-compatible client
/// sends and accepts, checked against a server on the loopback interface.
/// </summary>
public class SpeechToTextTests
{
    [Theory]
    [InlineData(SpeechProvider.None, "key", "", "", false)]
    [InlineData(SpeechProvider.OpenAI, "", "", "", false)]
    [InlineData(SpeechProvider.OpenAI, "key", "", "", true)]
    [InlineData(SpeechProvider.OpenAICompatible, "", "", "", false)]
    [InlineData(SpeechProvider.OpenAICompatible, "", "http://localhost:8080/v1", "", true)]
    [InlineData(SpeechProvider.AzureSpeech, "key", "", "", false)]
    [InlineData(SpeechProvider.AzureSpeech, "key", "", "westeurope", true)]
    [InlineData(SpeechProvider.WindowsSpeech, "", "", "", true)]
    public void Create_needs_what_the_provider_requires(SpeechProvider provider, string key, string endpoint, string region, bool expected)
    {
        var settings = new SpeechSettings { Provider = provider, ApiKey = key, Endpoint = endpoint, Region = region };
        Assert.Equal(expected, SpeechToTextFactory.Create(settings) is not null);
    }

    [Fact]
    public async Task Compatible_endpoint_gets_the_recording_and_returns_the_text()
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? path = null, auth = null, body = null;
        var served = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            path = ctx.Request.Url!.AbsolutePath;
            auth = ctx.Request.Headers["Authorization"];
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = Encoding.UTF8.GetString(ms.ToArray());
            }
            var reply = Encoding.UTF8.GetBytes("{\"text\":\" Zdravo svet \"}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(reply, 0, reply.Length);
            ctx.Response.Close();
        });

        var stt = SpeechToTextFactory.Create(new SpeechSettings
        {
            Provider = SpeechProvider.OpenAICompatible,
            Endpoint = prefix + "v1/",
            ApiKey = "secret",
            Model = "small",
        })!;

        var text = await stt.TranscribeAsync(new byte[] { 1, 2, 3, 4 }, "sl-SI", CancellationToken.None);
        await served;

        Assert.Equal("Zdravo svet", text);
        Assert.Equal("/v1/audio/transcriptions", path);
        Assert.Equal("Bearer secret", auth);
        // .NET Framework writes name=model without quotes; servers take either.
        var plain = body!.Replace("\"", "");
        Assert.Contains("name=model", plain);
        Assert.Contains("\r\n\r\nsmall\r\n", plain);
        // sl-SI is sent as the two-letter code the API takes.
        Assert.Contains("name=language", plain);
        Assert.Contains("\r\n\r\nsl\r\n", plain);
    }

    [Fact]
    public async Task A_refused_request_reports_the_servers_own_message()
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        var served = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            ctx.Response.StatusCode = 401;
            var reply = Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"Incorrect API key provided\"}}");
            ctx.Response.OutputStream.Write(reply, 0, reply.Length);
            ctx.Response.Close();
        });

        var stt = SpeechToTextFactory.Create(new SpeechSettings
        {
            Provider = SpeechProvider.OpenAICompatible,
            Endpoint = prefix,
        })!;

        var ex = await Assert.ThrowsAsync<SpeechToTextException>(
            () => stt.TranscribeAsync(new byte[] { 1 }, "", CancellationToken.None));
        await served;

        Assert.Contains("401", ex.Message);
        Assert.Contains("Incorrect API key provided", ex.Message);
    }

    [Theory]
    [InlineData("sl", "", SpeechToTextFactory.SlovenianPrompt)]
    [InlineData("sl-SI", "", SpeechToTextFactory.SlovenianPrompt)]
    [InlineData("SL", "", SpeechToTextFactory.SlovenianPrompt)]
    [InlineData("sl", "  VsAgentic, Roslyn  ", "VsAgentic, Roslyn")]   // the user's text wins
    [InlineData("en", "", "")]
    [InlineData("", "", "")]
    [InlineData("en", "Roslyn", "Roslyn")]
    public void The_prompt_is_the_users_text_or_the_slovenian_sample(string language, string prompt, string expected)
    {
        var s = new SpeechSettings { Language = language, Prompt = prompt };
        Assert.Equal(expected, SpeechToTextFactory.EffectivePrompt(s));
    }

    [Fact]
    public async Task The_prompt_travels_with_the_recording_as_utf8()
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? body = null;
        var served = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = Encoding.UTF8.GetString(ms.ToArray());
            }
            var reply = Encoding.UTF8.GetBytes("{\"text\":\"ok\"}");
            ctx.Response.OutputStream.Write(reply, 0, reply.Length);
            ctx.Response.Close();
        });

        var stt = SpeechToTextFactory.Create(new SpeechSettings
        {
            Provider = SpeechProvider.OpenAICompatible,
            Endpoint = prefix,
            Language = "sl",
        })!;
        await stt.TranscribeAsync(new byte[] { 1, 2 }, "sl", CancellationToken.None);
        await served;

        var plain = body!.Replace("\"", "");
        Assert.Contains("name=prompt", plain);
        // č, š and ž arrive intact, which is the point of the sample.
        Assert.Contains("poišči napake", plain);
    }

    [Fact]
    public async Task No_prompt_is_sent_for_a_language_without_a_sample()
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        string? body = null;
        var served = Task.Run(() =>
        {
            var ctx = listener.GetContext();
            using (var ms = new MemoryStream())
            {
                ctx.Request.InputStream.CopyTo(ms);
                body = Encoding.UTF8.GetString(ms.ToArray());
            }
            var reply = Encoding.UTF8.GetBytes("{\"text\":\"ok\"}");
            ctx.Response.OutputStream.Write(reply, 0, reply.Length);
            ctx.Response.Close();
        });

        var stt = SpeechToTextFactory.Create(new SpeechSettings
        {
            Provider = SpeechProvider.OpenAICompatible,
            Endpoint = prefix,
            Language = "en",
        })!;
        await stt.TranscribeAsync(new byte[] { 1, 2 }, "en", CancellationToken.None);
        await served;

        Assert.DoesNotContain("name=prompt", body!.Replace("\"", ""));
    }

    [Fact]
    public async Task Windows_recognizer_reads_a_recording_made_by_the_windows_voice()
    {
        // Needs an English recognizer, which a stock Windows has; without one there is nothing to check.
        if (!System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers()
                .Any(r => r.Culture.TwoLetterISOLanguageName == "en"))
            return;

        var wav = Speak("hello world, this is a test of dictation");
        var stt = SpeechToTextFactory.Create(new SpeechSettings { Provider = SpeechProvider.WindowsSpeech })!;
        var text = await stt.TranscribeAsync(wav, "en", CancellationToken.None);

        Assert.Contains("hello", text.ToLowerInvariant());
    }

    [Fact]
    public async Task Windows_recognizer_names_the_languages_it_has_when_yours_is_missing()
    {
        var stt = SpeechToTextFactory.Create(new SpeechSettings { Provider = SpeechProvider.WindowsSpeech })!;

        // Klingon is in no Windows speech pack.
        var ex = await Assert.ThrowsAsync<SpeechToTextException>(
            () => stt.TranscribeAsync(new byte[100], "tlh", CancellationToken.None));

        Assert.Contains("'tlh'", ex.Message);
        Assert.Contains("Installed:", ex.Message);
    }

    /// <summary>A 16 kHz mono 16-bit WAV of the Windows voice saying the text.</summary>
    private static byte[] Speak(string text)
    {
        using var ms = new MemoryStream();
        using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
        {
            synth.SetOutputToAudioStream(ms, new System.Speech.AudioFormat.SpeechAudioFormatInfo(
                16000, System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
            synth.Speak(text);
        }

        var pcm = ms.ToArray();
        using var wav = new MemoryStream();
        var w = new BinaryWriter(wav);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + pcm.Length);
        w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
        w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(pcm.Length);
        w.Write(pcm);
        w.Flush();
        return wav.ToArray();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
