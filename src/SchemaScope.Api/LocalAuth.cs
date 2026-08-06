using System.Security.Cryptography;
using System.Text;

namespace SchemaScope.Api;

/// <summary>
/// Guards the loopback API.
///
/// Listening on 127.0.0.1 with a random port is not a security boundary. Any
/// process on this machine can scan for the port, and a web page the user is
/// merely visiting can reach it through DNS rebinding. So every /api request
/// has to prove it came from our own window: it carries a token minted at
/// startup and handed to the UI in its launch URL.
/// </summary>
public sealed class LocalAuth(string token)
{
    public const string HeaderName = "X-SchemaScope-Token";

    /// <summary>EventSource cannot set headers, so SSE passes the token here instead.</summary>
    public const string QueryName = "k";

    /// <summary>
    /// Fixed token for <c>--server</c>, where the Vite dev proxy injects the
    /// header on every call. Never used by the packaged app.
    /// </summary>
    public const string DevToken = "schemascope-dev";

    public string Token { get; } = token;

    public static LocalAuth Mint() =>
        new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    /// <summary>
    /// Rejects anything addressed to a name other than our own loopback origin.
    /// A rebound DNS name arrives with the attacker's host in this header, so
    /// checking it is what stops a browser being used as a bridge onto here.
    /// </summary>
    public static bool IsLoopbackHost(HostString host) =>
        host.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    public bool IsAuthorised(HttpRequest request)
    {
        if (request.Headers.TryGetValue(HeaderName, out var header) && Matches(header.ToString()))
            return true;

        return request.Query.TryGetValue(QueryName, out var query) && Matches(query.ToString());
    }

    /// <summary>Length-independent compare; FixedTimeEquals returns false on a length mismatch.</summary>
    private bool Matches(string candidate) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(candidate),
        Encoding.UTF8.GetBytes(Token));
}
