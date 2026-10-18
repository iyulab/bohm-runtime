using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An identity service on a loopback port, as a native app sees one (OpenID Connect discovery, authorization code with
/// PKCE, refresh tokens that turn over on each use, revocation): sign-in agrees at once — or declines when told to — and
/// coming back to the app's loopback redirect is what the test's own HTTP client does in place of the person's browser.
/// </summary>
public sealed class FakeIdentity : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, (string Challenge, string? Nonce)> _codes = new(StringComparer.Ordinal);
    private static readonly string[] S256 = ["S256"];
    private int _issued;

    private FakeIdentity(WebApplication app, Uri address)
    {
        _app = app;
        Issuer = address.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>The issuer, as discovery names it (no trailing slash).</summary>
    public string Issuer { get; }

    /// <summary>The access token handed out last — what an API sees as the account's token.</summary>
    public string? AccessToken { get; private set; }

    /// <summary>The one refresh token still taken; using any other ends the session.</summary>
    public string? RefreshToken { get; private set; }

    /// <summary>How long an access token lasts, in seconds — short makes the app refresh before each use.</summary>
    public int ExpiresIn { get; set; } = 3600;

    /// <summary>When set, sign-in comes back declined (<c>access_denied</c>).</summary>
    public bool Declines { get; set; }

    /// <summary>How long redeeming a refresh token takes.</summary>
    public TimeSpan RefreshDelay { get; set; }

    /// <summary>How many times a refresh token was redeemed.</summary>
    public int Refreshes { get; private set; }

    /// <summary>The tokens revoked.</summary>
    public ConcurrentQueue<string> Revoked { get; } = new();

    public static async Task<FakeIdentity> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        FakeIdentity? self = null;
        app.Run(context => self!.HandleAsync(context));
        await app.StartAsync();
        self = new FakeIdentity(app, new Uri(app.Urls.First()));
        return self;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        switch (request.Path.Value)
        {
            case "/.well-known/openid-configuration":
                await JsonAsync(context, 200, new Dictionary<string, object>
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = Issuer + "/authorize",
                    ["token_endpoint"] = Issuer + "/token",
                    ["revocation_endpoint"] = Issuer + "/revoke",
                    ["code_challenge_methods_supported"] = S256,
                });
                return;
            case "/authorize":
                var query = request.Query;
                var back = query["redirect_uri"].ToString() + "?state=" + Uri.EscapeDataString(query["state"].ToString());
                if (Declines)
                {
                    context.Response.Redirect(back + "&error=access_denied");
                    return;
                }

                var code = "code-" + Interlocked.Increment(ref _issued);
                _codes[code] = (query["code_challenge"].ToString(), query["nonce"].FirstOrDefault());
                context.Response.Redirect(back + "&code=" + code);
                return;
            case "/token":
                var form = await request.ReadFormAsync();
                if (form["grant_type"] == "authorization_code")
                {
                    if (!_codes.TryRemove(form["code"].ToString(), out var granted)
                        || Challenge(form["code_verifier"].ToString()) != granted.Challenge)
                    {
                        await JsonAsync(context, 400, new { error = "invalid_grant" });
                        return;
                    }

                    await TokensAsync(context, granted.Nonce);
                    return;
                }

                if (form["grant_type"] == "refresh_token" && RefreshToken is { } current && form["refresh_token"] == current)
                {
                    await Task.Delay(RefreshDelay);   // a slow answer: a second refresh begun meanwhile would use the same token
                    Refreshes++;
                    await TokensAsync(context, nonce: null);
                    return;
                }

                RefreshToken = null;   // an old refresh token used again: the session ends
                await JsonAsync(context, 400, new { error = "invalid_grant" });
                return;
            case "/revoke":
                var revoked = (await request.ReadFormAsync())["token"].ToString();
                Revoked.Enqueue(revoked);
                if (revoked == RefreshToken) RefreshToken = null;
                context.Response.StatusCode = 200;
                return;
            default:
                context.Response.StatusCode = 404;
                return;
        }
    }

    private async Task TokensAsync(HttpContext context, string? nonce)
    {
        var n = Interlocked.Increment(ref _issued);
        AccessToken = "access-" + n;
        RefreshToken = "refresh-" + n;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object> { ["iss"] = Issuer, ["aud"] = "bohm-desktop", ["sub"] = "ava", ["iat"] = now, ["exp"] = now + 600, ["email"] = "ava@example.com" };
        if (nonce is not null) claims["nonce"] = nonce;
        await JsonAsync(context, 200, new Dictionary<string, object>
        {
            ["access_token"] = AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = ExpiresIn,
            ["refresh_token"] = RefreshToken,
            ["id_token"] = Part("""{"alg":"RS256","typ":"JWT"}""") + "." + Part(JsonSerializer.Serialize(claims)) + ".unchecked",
        });
    }

    private static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task JsonAsync(HttpContext context, int status, object body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
