using System;
using System.IO;
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

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
