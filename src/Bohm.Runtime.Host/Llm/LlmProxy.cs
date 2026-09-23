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
/// When no key is connected, or the provider cannot be reached, the application gets an error in
/// the provider's own shape, so its existing error handling shows it instead of breaking.
/// </remarks>
internal static class LlmProxy
{
    public const string PathPrefix = "/__bohm/llm/";

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
        var sessions = context.RequestServices.GetRequiredService<AppSessions>();
        if (!sessions.IsSession(appId, request.Cookies[AdoptedAppServing.SessionCookie]) || !SameOriginOrAbsent(request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var rest = request.Path.Value![PathPrefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var provider = LlmProviders.ByHost(slash < 0 ? rest : rest[..slash]);
        if (provider is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var key = context.RequestServices.GetRequiredService<ICredentialVault>().Read(provider.VaultName);
        if (string.IsNullOrEmpty(key))
        {
            app.NeedsKey(provider.Id);
            await WriteErrorAsync(response, provider, HttpStatusCode.Unauthorized, "no_key",
                $"No {provider.DisplayName} API key is connected. Connect one in Bohm to use AI in this app.").ConfigureAwait(false);
            return;
        }

        var options = context.RequestServices.GetRequiredService<RuntimeHostOptions>();
        var upstreamBase = options.LlmEndpoints?.GetValueOrDefault(provider.Host) ?? new Uri($"https://{provider.Host}/");
        var path = slash < 0 ? "" : rest[(slash + 1)..];
        var query = QueryHelpers.ParseQuery(request.QueryString.Value);
        if (provider.Style == KeyStyle.Google && query.ContainsKey("key")) query["key"] = key;
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
        if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            upstream.Content = new StreamContent(request.Body);
            if (request.ContentType is { } contentType) upstream.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        foreach (var (name, values) in request.Headers)
        {
            if (NotForwarded.Contains(name) || name.StartsWith("Sec-", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            upstream.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
        }

        switch (provider.Style)
        {
            case KeyStyle.Bearer: upstream.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}"); break;
            case KeyStyle.ApiKeyHeader: upstream.Headers.TryAddWithoutValidation("x-api-key", key); break;
            case KeyStyle.Google: upstream.Headers.TryAddWithoutValidation("x-goog-api-key", key); break;
        }

        var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(LlmProxy));
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

    private static bool SameOriginOrAbsent(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

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
                ["error"] = new Dictionary<string, object> { ["code"] = (int)status, ["message"] = message, ["status"] = type == "no_key" ? "UNAUTHENTICATED" : "UNAVAILABLE" },
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
