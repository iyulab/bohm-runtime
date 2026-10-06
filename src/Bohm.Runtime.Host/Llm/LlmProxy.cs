using System.Net;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Adoption;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.WebUtilities;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Relays an adopted application's AI provider calls. The injected script sends a request for
/// <c>https://api.openai.com/v1/chat/completions</c> to the application's own origin as
/// <c>/__bohm/llm/api.openai.com/v1/chat/completions</c>; the runtime replaces the placeholder the
/// application holds with the real key from the vault and forwards the request unchanged
/// otherwise, streaming the provider's answer back as it arrives.
/// </summary>
/// <remarks>
/// When no key is connected but the organization's model server (<see cref="CompanyModelOptions"/>) or
/// a model on this computer (<see cref="LocalModelOptions"/>) is — the organization's first — a chat
/// request in the OpenAI, Anthropic or Gemini shape is answered by that model instead
/// (<see cref="ChatBridges"/>).
/// Otherwise, when no key is connected or the provider cannot be reached, the application gets an
/// error in the provider's own shape, so its existing error handling shows it instead of breaking.
/// </remarks>
internal static class LlmProxy
{
    public const string PathPrefix = "/__bohm/llm/";

    /// <summary>
    /// The longest answer the model on this computer writes when a request sets no limit — a little
    /// over two minutes at the slowest rate measured on an office PC (under 4 tokens a second), well
    /// inside the local server's five-minute request limit.
    /// </summary>
    public const int DefaultLocalMaxOutputTokens = 512;

    /// <summary>The path segment, in place of a provider's host, under which an application's own calls to the organization's model server arrive.</summary>
    public const string CompanyModelSegment = "company-model";

    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Origin", "Referer", "Connection", "Content-Length", "Transfer-Encoding", "Keep-Alive",
        "Authorization", "x-api-key", "x-goog-api-key", AdoptedAppServing.RequestHeader,
    };

    private static readonly HashSet<string> NotReturned = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Transfer-Encoding", "Keep-Alive", "Set-Cookie", "Content-Length",
        "Access-Control-Allow-Origin", "Access-Control-Allow-Credentials", "Access-Control-Allow-Headers", "Access-Control-Allow-Methods",
    };

    public static async Task HandleAsync(HttpContext context, string appId, OpenApp app)
    {
        var request = context.Request;
        var response = context.Response;

        // Requests come from the application's own fetch calls, which cannot carry the script's
        // custom header; the origin-scoped session cookie and the Origin check stand in for it.
        if (!PageRequests.IsFromTheApp(context, appId))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var rest = request.Path.Value![PathPrefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var providerPath = slash < 0 ? "" : rest[(slash + 1)..];
        var company = context.RequestServices.GetRequiredService<CompanyModel>();
        if ((slash < 0 ? rest : rest[..slash]) == CompanyModelSegment)
        {
            // The application calls the organization's model server itself: relayed as it is, with the server's key.
            if (company.Current is not { } server)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await RelayAsync(context, CompanyModelShape(server), server.Endpoint, providerPath,
                QueryHelpers.ParseQuery(request.QueryString.Value), company.Key).ConfigureAwait(false);
            return;
        }

        var provider = LlmProviders.ByHost(slash < 0 ? rest : rest[..slash]);
        if (provider is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var key = context.RequestServices.GetRequiredService<ICredentialVault>().Read(provider.VaultName);
        var local = context.RequestServices.GetRequiredService<LocalModel>();
        if (string.IsNullOrEmpty(key) && IsTranscription(provider, request.Method, providerPath)
            && await TranscribeKeylessAsync(context, app, provider, company, local.Configured).ConfigureAwait(false))
            return;

        if (string.IsNullOrEmpty(key) && (company.Configured || local.Configured))
        {
            if (ChatBridges.For(provider, request.Method, providerPath) is { } bridge)
            {
                if (company.Client() is { } organizations)
                    await AnswerByCompanyModelAsync(context, provider, organizations, bridge, providerPath).ConfigureAwait(false);
                else
                    await AnswerLocallyAsync(context, provider, local, bridge, providerPath).ConfigureAwait(false);
                return;
            }

            if (ChatBridges.Unsupported(provider, request.Method, providerPath) is { } why)
            {
                await WriteErrorAsync(response, provider, HttpStatusCode.NotImplemented, "local_model_unsupported", why).ConfigureAwait(false);
                return;
            }
        }

        if (string.IsNullOrEmpty(key))
        {
            app.NeedsKey(provider.Id);
            await WriteErrorAsync(response, provider, HttpStatusCode.Unauthorized, "no_key",
                $"No {provider.DisplayName} API key is connected. Connect one in Bohm to use AI in this app.").ConfigureAwait(false);
            return;
        }

        var options = context.RequestServices.GetRequiredService<RuntimeHostOptions>();
        var upstreamBase = options.LlmEndpoints?.GetValueOrDefault(provider.Host) ?? new Uri($"https://{provider.Host}/");
        var query = QueryHelpers.ParseQuery(request.QueryString.Value);
        if (provider.Style == KeyStyle.Google && query.ContainsKey("key")) query["key"] = key;
        await RelayAsync(context, provider, upstreamBase, providerPath, query, key).ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards the application's request to <paramref name="upstreamBase"/> + <paramref name="path"/> with
    /// <paramref name="key"/> presented the provider's way (nothing when <see langword="null"/>), and
    /// streams the answer back. Counted as sent, whether or not it is answered.
    /// </summary>
    /// <param name="content">The body to send in place of the application's own, when the request was rebuilt.</param>
    private static async Task RelayAsync(HttpContext context, LlmProvider provider, Uri upstreamBase, string path,
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> query, string? key, HttpContent? content = null)
    {
        var request = context.Request;
        var response = context.Response;
        // The path comes from the page, so it is appended, never resolved: relative resolution would
        // let "//other.host/…" or an absolute URL choose where the key goes.
        if (path.Contains('\\', StringComparison.Ordinal) || path.Split('/').Any(segment => segment is ".." or "."))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var target = new UriBuilder(upstreamBase)
        {
            Path = upstreamBase.AbsolutePath.TrimEnd('/') + "/" + path.TrimStart('/'),
            Query = QueryString.Create(query).ToUriComponent().TrimStart('?'),
        }.Uri;

        // The key is attached only to a request whose destination is the provider (or its configured stand-in).
        if (!string.Equals(target.Host, upstreamBase.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != upstreamBase.Port || target.Scheme != upstreamBase.Scheme)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), target);
        if (content is not null)
        {
            upstream.Content = content;
        }
        else if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            upstream.Content = new StreamContent(request.Body);
            if (request.ContentType is { } contentType) upstream.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        foreach (var (name, values) in request.Headers)
        {
            if (NotForwarded.Contains(name) || name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            upstream.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }

        if (key is not null)
        {
            switch (provider.Style)
            {
                case KeyStyle.Bearer: upstream.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}"); break;
                case KeyStyle.ApiKeyHeader: upstream.Headers.TryAddWithoutValidation("x-api-key", key); break;
                case KeyStyle.Google: upstream.Headers.TryAddWithoutValidation("x-goog-api-key", key); break;
            }
        }

        var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(LlmProxy));
        // Counted when it is sent, whether or not the provider answers: the application's data left.
        context.RequestServices.GetRequiredService<Egress>().Sent(target.Authority);
        HttpResponseMessage answer;
        try
        {
            answer = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteErrorAsync(response, provider, HttpStatusCode.BadGateway, "unreachable",
                $"{provider.DisplayName} could not be reached. This computer may be offline.").ConfigureAwait(false);
            return;
        }

        using (answer)
        {
            response.StatusCode = (int)answer.StatusCode;
            foreach (var (name, values) in answer.Headers.Concat(answer.Content.Headers))
                if (!NotReturned.Contains(name)) response.Headers[name] = values.ToArray();

            // Streamed answers (server-sent events) must reach the page as they arrive.
            context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            await using var body = await answer.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
                await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether the request asks OpenAI's API to turn a recording into text.</summary>
    private static bool IsTranscription(LlmProvider provider, string method, string path) =>
        provider.Id == "openai" && HttpMethods.IsPost(method) && path.Trim('/') == "v1/audio/transcriptions";

    /// <summary>
    /// No OpenAI key is connected and a recording is sent to OpenAI's transcription API: the organization's server
    /// turns it into text when it lists a speech model, otherwise the speech model on this computer when it is here.
    /// Without either, while some AI answers without a key, the application is told none is here yet — and the shell
    /// learns it, to offer getting the model on this computer.
    /// </summary>
    /// <returns>Whether the request was answered; <see langword="false"/> leaves it to the key check, as with no AI at all.</returns>
    private static async Task<bool> TranscribeKeylessAsync(HttpContext context, OpenApp app, LlmProvider provider, CompanyModel company, bool localChat)
    {
        var request = context.Request;
        var speech = context.RequestServices.GetRequiredService<LocalSpeech>();
        if (company.Current is { } server && await company.TranscriptionModelAsync(context.RequestAborted).ConfigureAwait(false) is { } model)
        {
            if (await ReadRecordingFormAsync(context, provider).ConfigureAwait(false) is { } form)
                await TranscribeByCompanyModelAsync(context, company, server, model, form).ConfigureAwait(false);
            return true;
        }

        if (await speech.DownloadedAsync(context.RequestAborted).ConfigureAwait(false))
        {
            if (await ReadRecordingFormAsync(context, provider).ConfigureAwait(false) is { } form)
                await TranscribeLocallyAsync(context, provider, speech, form).ConfigureAwait(false);
            return true;
        }

        if (!company.Configured && !localChat) return false;
        if (speech.Supported) app.NeedsSpeechModel();
        await WriteErrorAsync(context.Response, provider, HttpStatusCode.ServiceUnavailable, "local_model_unavailable",
            "No model here turns speech into text yet. Keep the recording and try again later.").ConfigureAwait(false);
        return true;
    }

    /// <summary>The recording's form, or <see langword="null"/> after answering that it is not one.</summary>
    private static async Task<IFormCollection?> ReadRecordingFormAsync(HttpContext context, LlmProvider provider)
    {
        if (context.Request.HasFormContentType && await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false) is { Files.Count: > 0 } form)
            return form;

        await WriteErrorAsync(context.Response, provider, HttpStatusCode.BadRequest, "invalid_request",
            "Send the recording as multipart form data, with the fields file and model.").ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// The speech model on this computer turns the recording into text, answered in OpenAI's shape (<c>{"text": …}</c>,
    /// or the bare text for <c>response_format=text</c>). Nothing leaves the computer.
    /// </summary>
    private static async Task TranscribeLocallyAsync(HttpContext context, LlmProvider provider, LocalSpeech speech, IFormCollection form)
    {
        var file = form.Files.GetFile("file") ?? form.Files[0];
        using var audio = new MemoryStream();
        await file.CopyToAsync(audio, context.RequestAborted).ConfigureAwait(false);
        var language = form["language"].ToString() is { Length: > 0 } named ? named : null;
        string text;
        try
        {
            text = await speech.TranscribeAsync(audio.ToArray(), language, context.RequestAborted).ConfigureAwait(false);
        }
        catch (LocalModelUnavailableException e)
        {
            await WriteErrorAsync(context.Response, provider, HttpStatusCode.ServiceUnavailable, "local_model_unavailable", e.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or FormatException && !context.RequestAborted.IsCancellationRequested)
        {
            await WriteErrorAsync(context.Response, provider, HttpStatusCode.BadRequest, "invalid_request",
                $"The recording could not be read: {e.Message}").ConfigureAwait(false);
            return;
        }

        if (form["response_format"].ToString() == "text")
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(text, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new { text }), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The organization's server turns the recording into text with its speech model — the same form, with that
    /// model named, so the answer comes back in OpenAI's shape.
    /// </summary>
    private static async Task TranscribeByCompanyModelAsync(HttpContext context, CompanyModel company, CompanyModelOptions server, string model, IFormCollection form)
    {
        using var content = new MultipartFormDataContent();
        foreach (var (name, values) in form)
        {
            if (name.Equals("model", StringComparison.Ordinal)) continue;
            foreach (var value in values) content.Add(new StringContent(value ?? ""), name);
        }

        content.Add(new StringContent(model), "model");
        foreach (var file in form.Files)
        {
            var part = new StreamContent(file.OpenReadStream());
            if (System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(file.ContentType, out var type)) part.Headers.ContentType = type;
            content.Add(part, file.Name, string.IsNullOrEmpty(file.FileName) ? "recording" : file.FileName);
        }

        await RelayAsync(context, CompanyModelShape(server), server.Endpoint, "audio/transcriptions", [], company.Key, content).ConfigureAwait(false);
    }

    /// <summary>
    /// No key is connected for the provider, and a model on this computer is: the request is
    /// answered by it, converted from and back to the provider's shape. Nothing leaves the computer,
    /// so nothing is recorded as sent.
    /// </summary>
    private static async Task AnswerLocallyAsync(HttpContext context, LlmProvider provider, LocalModel local, IChatBridge bridge, string path)
    {
        if (await ParseAsync(context, provider, bridge, path).ConfigureAwait(false) is not { } parsed) return;

        // A request that sets no limit on the answer's length gets one: a provider stops on its own
        // well within its time, a small model on a CPU writes a few tokens a second and can run past
        // the local server's request limit before it ends. A limit the application sets is kept.
        parsed.Options.MaxOutputTokens ??= DefaultLocalMaxOutputTokens;

        Microsoft.Extensions.AI.IChatClient model;
        try
        {
            model = await local.GetAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (LocalModelUnavailableException e)
        {
            await WriteErrorAsync(context.Response, provider, HttpStatusCode.ServiceUnavailable, "local_model_unavailable", e.Message).ConfigureAwait(false);
            return;
        }

        try
        {
            await bridge.AnswerAsync(context, new JsonModeChatClient(model), parsed).ConfigureAwait(false);
        }
        catch (Exception e) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            // The model stopped without an answer — the local server's request limit, or the server
            // itself. The application gets an error it can show, in the provider's shape, instead of
            // an empty 500. (Once a stream has begun, its end is all that can be said.)
            await WriteErrorAsync(context.Response, provider, HttpStatusCode.ServiceUnavailable, "local_model_failed",
                $"The AI model on this computer did not finish the answer: {e.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// No key is connected for the provider, and the organization's model server is set: the request
    /// is answered by it, converted from and back to the provider's shape. It leaves this computer for
    /// the organization's network, so it is counted as sent to the server's host.
    /// </summary>
    private static async Task AnswerByCompanyModelAsync(HttpContext context, LlmProvider provider, Microsoft.Extensions.AI.IChatClient model, IChatBridge bridge, string path)
    {
        if (await ParseAsync(context, provider, bridge, path).ConfigureAwait(false) is not { } parsed) return;
        try
        {
            await bridge.AnswerAsync(context, new JsonModeChatClient(model), parsed).ConfigureAwait(false);
        }
        catch (Exception e) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            // The server could not be reached, or refused the request (an input it cannot read, a model it does not have). The application gets an error it can show, in
            // the provider's shape; the server's own message is kept, since it says why.
            await WriteErrorAsync(context.Response, provider, HttpStatusCode.ServiceUnavailable, "company_model_failed",
                $"The organization's AI model server could not answer this request: {e.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>The application's chat request in the bridge's terms, or <see langword="null"/> after answering with why it cannot be.</summary>
    private static async Task<BridgedChat?> ParseAsync(HttpContext context, LlmProvider provider, IChatBridge bridge, string path)
    {
        try
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted).ConfigureAwait(false);
            return bridge.Parse(body.GetBuffer().AsSpan(0, (int)body.Length), path);
        }
        catch (FormatException e)
        {
            await WriteErrorAsync(context.Response, provider, HttpStatusCode.BadRequest, "invalid_request", e.Message).ConfigureAwait(false);
        }
        catch (NotSupportedException e)
        {
            await WriteErrorAsync(context.Response, provider, HttpStatusCode.NotImplemented, "local_model_unsupported", e.Message).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>The organization's model server as a provider for relaying and for errors: OpenAI-shaped, keyed with a bearer token.</summary>
    private static LlmProvider CompanyModelShape(CompanyModelOptions server) =>
        new(CompanyModelSegment, server.Endpoint.Authority, "The organization's AI model server", KeyStyle.Bearer, "");

    /// <summary>An error in the shape the provider's own API uses, so the application's error handling applies.</summary>
    private static Task WriteErrorAsync(HttpResponse response, LlmProvider provider, HttpStatusCode status, string type, string message)
    {
        response.StatusCode = (int)status;
        response.ContentType = "application/json";
        object body = provider.Style switch
        {
            KeyStyle.ApiKeyHeader => new Dictionary<string, object>
            {
                ["type"] = "error",
                ["error"] = new Dictionary<string, object> { ["type"] = $"bohm_{type}", ["message"] = message },
            },
            KeyStyle.Google => new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object> { ["code"] = (int)status, ["message"] = message, ["status"] = status switch
                {
                    HttpStatusCode.Unauthorized => "UNAUTHENTICATED",
                    HttpStatusCode.BadRequest => "INVALID_ARGUMENT",
                    HttpStatusCode.NotImplemented => "UNIMPLEMENTED",
                    _ => "UNAVAILABLE",
                } },
            },
            _ => new Dictionary<string, object>
            {
                ["error"] = new Dictionary<string, object> { ["message"] = message, ["type"] = $"bohm_{type}", ["code"] = type },
            },
        };
        return response.WriteAsync(JsonSerializer.Serialize(body, LlmJson.Default.DictionaryStringObject));
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, object>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(int))]
internal sealed partial class LlmJson : System.Text.Json.Serialization.JsonSerializerContext;
