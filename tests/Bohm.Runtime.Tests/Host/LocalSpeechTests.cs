using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An application's recording sent to OpenAI's transcription API with no key connected, turned into text by the speech
/// model on this computer — once the person has got it; before that the application is told so and the shell learns it.
/// </summary>
public sealed class LocalSpeechTests
{
    private const string App = "<!doctype html><title>Voice log</title>";
    private const string Transcriptions = "/__bohm/llm/api.openai.com/v1/audio/transcriptions";

    [Fact]
    public async Task Before_the_speech_model_is_here_a_recording_is_refused_and_the_need_is_reported_without_a_download()
    {
        var source = new StandIn();
        await using var host = await StartAsync(source, keyless: true);
        var app = await host.AdoptAsync(App);

        using var response = await PostRecordingAsync(host, app, [1, 2, 3]);

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        Assert.Equal("local_model_unavailable", ErrorCode(await response.Content.ReadAsStringAsync()));
        Assert.Equal(0, source.Loads);
        Assert.Empty(Sent(await EgressAsync(host)));
        var status = JsonDocument.Parse(await host.ControlClient().GetStringAsync($"/__control/apps/{app}/status")).RootElement;
        Assert.True(status.GetProperty("needsSpeechModel").GetBoolean());
    }

    [Fact]
    public async Task With_the_speech_model_here_the_recording_is_turned_into_text_on_this_computer_in_OpenAIs_shape()
    {
        var source = new StandIn { Downloaded = true };
        await using var host = await StartAsync(source, keyless: true);
        var app = await host.AdoptAsync(App);

        using var json = await PostRecordingAsync(host, app, [7, 8, 9], ("model", "whisper-1"), ("language", "ko"));
        using var text = await PostRecordingAsync(host, app, [7, 8, 9], ("model", "whisper-1"), ("response_format", "text"));

        HttpAssert.Status(HttpStatusCode.OK, json);
        Assert.Equal("heard 3 bytes in ko", JsonDocument.Parse(await json.Content.ReadAsStringAsync()).RootElement.GetProperty("text").GetString());
        HttpAssert.Status(HttpStatusCode.OK, text);
        Assert.Equal("heard 3 bytes in (any)", await text.Content.ReadAsStringAsync());
        Assert.Equal(1, source.Loads);                                     // loaded once, then kept
        Assert.All(source.LoadedWithDownload, Assert.False);               // an application never causes a download
        Assert.Empty(Sent(await EgressAsync(host)));                       // nothing left the computer
    }

    [Fact]
    public async Task The_speech_model_is_here_for_an_application_even_with_no_other_AI_answering_without_a_key()
    {
        await using var host = await StartAsync(new StandIn { Downloaded = true }, keyless: false);
        var app = await host.AdoptAsync(App);

        using var response = await PostRecordingAsync(host, app, [1]);

        HttpAssert.Status(HttpStatusCode.OK, response);
    }

    [Fact]
    public async Task With_no_AI_answering_without_a_key_and_no_speech_model_the_app_is_asked_for_a_key_as_before()
    {
        await using var host = await StartAsync(new StandIn(), keyless: false);
        var app = await host.AdoptAsync(App);

        using var response = await PostRecordingAsync(host, app, [1]);

        HttpAssert.Status(HttpStatusCode.Unauthorized, response);
    }

    [Fact]
    public async Task Getting_the_speech_model_is_counted_as_sent_to_its_host_and_leaves_it_loaded()
    {
        var source = new StandIn();
        await using var host = await StartAsync(source, keyless: true);

        var before = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/speech-model")).RootElement;
        Assert.True(before.GetProperty("supported").GetBoolean());
        Assert.False(before.GetProperty("downloaded").GetBoolean());
        using (var described = await host.ControlClient().PostAsync("/__control/llm/speech-model/describe", null))
            Assert.Equal(253, JsonDocument.Parse(await described.Content.ReadAsStringAsync()).RootElement.GetProperty("sizeBytes").GetInt64());

        using (var started = await host.ControlClient().PostAsync("/__control/llm/speech-model/download", null))
            HttpAssert.Status(HttpStatusCode.Accepted, started);
        await host.Services.GetRequiredService<LocalSpeech>().Settled;

        var after = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/speech-model")).RootElement;
        Assert.True(after.GetProperty("downloaded").GetBoolean());
        Assert.True(after.GetProperty("loaded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("download").ValueKind);
        Assert.Equal(["speech.example"], Sent(await EgressAsync(host)));
        Assert.Equal([true], source.LoadedWithDownload);

        using (var again = await host.ControlClient().PostAsync("/__control/llm/speech-model/download", null))
            HttpAssert.Status(HttpStatusCode.OK, again);                    // here already: nothing to get
        var app = await host.AdoptAsync(App);
        using var response = await PostRecordingAsync(host, app, [1, 2]);
        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal(1, source.Loads);                                     // the model the download loaded answers
    }

    [Fact]
    public async Task A_download_that_fails_says_why_and_the_model_stays_missing()
    {
        await using var host = await StartAsync(new StandIn { FailDownload = new HttpRequestException("offline") }, keyless: true);

        using (var started = await host.ControlClient().PostAsync("/__control/llm/speech-model/download", null))
            HttpAssert.Status(HttpStatusCode.Accepted, started);
        await host.Services.GetRequiredService<LocalSpeech>().Settled;

        var view = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/speech-model")).RootElement;
        Assert.False(view.GetProperty("downloaded").GetBoolean());
        Assert.Equal("unreachable", view.GetProperty("download").GetProperty("failure").GetString());
    }

    [Fact]
    public async Task A_copy_without_the_speech_runtime_says_so_and_refuses_a_download()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with { LocalModel = new LocalModelOptions { ModelPath = "unused.gguf", Client = new FakeChatModel() } });

        var view = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/speech-model")).RootElement;
        Assert.False(view.GetProperty("supported").GetBoolean());
        using var started = await host.ControlClient().PostAsync("/__control/llm/speech-model/download", null);
        HttpAssert.Status(HttpStatusCode.Conflict, started);

        var app = await host.AdoptAsync(App);
        using var response = await PostRecordingAsync(host, app, [1]);
        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        var status = JsonDocument.Parse(await host.ControlClient().GetStringAsync($"/__control/apps/{app}/status")).RootElement;
        Assert.False(status.GetProperty("needsSpeechModel").GetBoolean()); // nothing the shell could offer
    }

    [Fact]
    public async Task A_browser_recording_is_turned_into_text_by_the_real_speech_model()
    {
        // The model (about 970 MB) is not got by the suite: it must be in the model library's cache already.
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BOHM_TEST_SPEECH") != "1", "BOHM_TEST_SPEECH is not set to 1.");
        var native = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
        Assert.SkipUnless(OperatingSystem.IsWindows() && File.Exists(Path.Combine(native, "onnxruntime.dll")), "No native ONNX Runtime next to the tests.");
        var source = new LMSupplySpeechModelSource(native);
        Assert.SkipUnless(await source.IsDownloadedAsync(CancellationToken.None), "The speech model is not in the model library's cache.");
        await using var host = await RunningHost.StartAsync(configure: o => o with
        {
            SpeechModelSource = source,
            LocalModel = new LocalModelOptions { ModelPath = "unused.gguf", Client = new FakeChatModel() },
        });
        var app = await host.AdoptAsync(App);

        using var response = await PostRecordingAsync(host, app,
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Host", "Speech", "spoken-ko.webm")), ("model", "whisper-1"), ("language", "ko"));

        HttpAssert.Status(HttpStatusCode.OK, response);
        var text = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("text").GetString();
        Assert.Contains("소화기", text, StringComparison.Ordinal);
        Assert.Contains("교체", text, StringComparison.Ordinal);
    }

    private static Task<RunningHost> StartAsync(StandIn source, bool keyless) =>
        RunningHost.StartAsync(configure: o => o with
        {
            SpeechModelSource = source,
            LocalModel = keyless ? new LocalModelOptions { ModelPath = "unused.gguf", Client = new FakeChatModel() } : null,
        });

    private static async Task<HttpResponseMessage> PostRecordingAsync(RunningHost host, string app, byte[] recording, params (string Name, string Value)[] fields)
    {
        using var load = await host.ClientForApp(app).GetAsync("/");
        var setCookie = Assert.Single(load.Headers.GetValues("Set-Cookie"));
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(recording);
        file.Headers.ContentType = new("audio/webm");
        form.Add(file, "file", "recording.webm");
        foreach (var (name, value) in fields) form.Add(new StringContent(value), name);
        var request = new HttpRequestMessage(HttpMethod.Post, Transcriptions) { Content = form };
        request.Headers.Add("Cookie", setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)]);
        request.Headers.Authorization = new("Bearer", $"bohm-key-{app}");
        return await host.ClientForApp(app).SendAsync(request);
    }

    private static string? ErrorCode(string body) => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString();

    private static async Task<JsonElement> EgressAsync(RunningHost host) =>
        JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.Clone();

    private static List<string?> Sent(JsonElement egress) => egress.GetProperty("sent").EnumerateArray().Select(s => s.GetProperty("host").GetString()).ToList();

    private sealed class StandIn : ISpeechModelSource
    {
        public bool Downloaded { get; set; }

        public Exception? FailDownload { get; init; }

        public int Loads;

        public List<bool> LoadedWithDownload { get; } = [];

        public string Host => "speech.example";

        public Task<long> DownloadSizeAsync(CancellationToken cancellationToken) => Task.FromResult(253L);

        public Task<bool> IsDownloadedAsync(CancellationToken cancellationToken) => Task.FromResult(Downloaded);

        public Task<ISpeechToText> LoadAsync(bool download, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            lock (LoadedWithDownload) LoadedWithDownload.Add(download);
            if (!Downloaded && !download) throw new InvalidOperationException("not here");
            if (download && FailDownload is { } failure) throw failure;
            progress?.Report(new(253, 253));
            Downloaded = true;
            Interlocked.Increment(ref Loads);
            return Task.FromResult<ISpeechToText>(new Heard());
        }

        private sealed class Heard : ISpeechToText
        {
            public Task<string> TranscribeAsync(byte[] audio, string? language, CancellationToken cancellationToken) =>
                Task.FromResult($"heard {audio.Length} bytes in {language ?? "(any)"}");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
