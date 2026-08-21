using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DistSear.Coordinator.Security;

/// <summary>
/// Authenticates service callers presenting an <c>X-Api-Key</c> header.
///
/// Exists alongside Entra ID rather than instead of it: an interactive user carries a token with
/// their group memberships, while a background service has no user and needs a credential of its
/// own. Both end up as claims, so document-level filtering downstream cannot tell them apart.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly IApiKeyStore _keys;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyStore keys)
        : base(options, logger, encoder) => _keys = keys;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var presented) || presented.Count == 0)
        {
            // No key offered. NoResult rather than Fail, so another scheme still gets its turn.
            return AuthenticateResult.NoResult();
        }

        var key = presented[0];

        if (string.IsNullOrWhiteSpace(key))
        {
            return AuthenticateResult.Fail("Empty API key.");
        }

        var record = await _keys.FindAsync(key, Context.RequestAborted);

        if (record is null)
        {
            // Deliberately vague: distinguishing "unknown key" from "revoked key" would tell an
            // attacker which of their guesses had once been valid.
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, record.Name),
            new("api_key_name", record.Name)
        };

        foreach (var group in record.Groups)
        {
            claims.Add(new Claim("groups", group));
        }

        if (record.CanWrite)
        {
            claims.Add(new Claim("scope", "search.write"));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
