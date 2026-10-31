using System.Net.Sockets;
using System.Security.Authentication;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Why a server was not reached, as far as the failure says — the one reading every way of asking a server a question shares
/// (the answer model's check, the list of a server's models, a provider's check), so a wrong address is told the same wherever
/// it was typed. The values name what to look at: the address's spelling, the port and whether the server runs, the scheme
/// and the server's certificate, or the network. Read from the failure's kind, never its message, which the system words in
/// its own language.
/// </summary>
internal static class Unreached
{
    /// <summary>The server's name does not resolve.</summary>
    public const string HostNotFound = "host-not-found";

    /// <summary>Nothing accepts connections at the address.</summary>
    public const string ConnectionRefused = "connection-refused";

    /// <summary>No answer came in time.</summary>
    public const string NoAnswer = "no-answer";

    /// <summary>
    /// Something answered at the address, but no secure connection was made with it: the address says <c>https</c> and the server
    /// speaks plain <c>http</c>, or its certificate is not one this computer trusts. The failure does not tell the two apart.
    /// </summary>
    public const string SecureConnection = "secure-connection";

    /// <summary>
    /// Whether the failure is the road to the server failing — nothing it said: the connection, the name, the secure handshake or
    /// the time — whatever layer raised it and however many times that layer wrapped it (a retry that gave up raises an
    /// <see cref="AggregateException"/>, a provider's client its own exception around the <see cref="HttpRequestException"/>).
    /// </summary>
    public static bool Is(Exception failure) =>
        Chain(failure).Any(e => e is OperationCanceledException or TimeoutException or HttpRequestException or SocketException or AuthenticationException);

    /// <summary>
    /// Why the server was not reached, or <see langword="null"/> when the failure says none of the values here.
    /// What is more particular is read first: the secure handshake and the name, then the socket's own code, then the time.
    /// </summary>
    public static string? Of(Exception failure)
    {
        var chain = Chain(failure).ToList();
        if (chain.Any(e => e is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }))
            return SecureConnection;
        if (chain.Any(e => e is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }))
            return HostNotFound;
        foreach (var socket in chain.OfType<SocketException>())
        {
            var why = socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => ConnectionRefused,
                SocketError.HostNotFound or SocketError.NoData => HostNotFound,
                SocketError.TimedOut => NoAnswer,
                _ => null,
            };
            if (why is not null) return why;
        }

        return chain.Any(e => e is OperationCanceledException or TimeoutException) ? NoAnswer : null;
    }

    /// <summary>The failure and what it wraps, outermost first — every branch of an <see cref="AggregateException"/> as well as the inner exception chain.</summary>
    private static IEnumerable<Exception> Chain(Exception failure)
    {
        var pending = new Stack<Exception>();
        pending.Push(failure);
        for (var seen = 0; pending.Count > 0 && seen < 32; seen++)
        {
            var e = pending.Pop();
            yield return e;
            if (e is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Reverse())
                    pending.Push(inner);
            else if (e.InnerException is { } next)
                pending.Push(next);
        }
    }
}
