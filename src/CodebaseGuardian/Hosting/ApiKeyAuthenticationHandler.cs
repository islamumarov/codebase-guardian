using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodebaseGuardian.Hosting;

/// <summary>Authenticates <c>Authorization: Bearer &lt;key&gt;</c> against the configured API keys. Keys are never logged.</summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<HttpAuthOptions> auth)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "GuardianApiKey";
    private const string BearerPrefix = "Bearer ";

    private readonly (string Principal, byte[] Hash)[] _keys =
        [.. auth.Value.ApiKeys.Select(pair => (pair.Key, SHA256.HashData(Encoding.UTF8.GetBytes(pair.Value))))];

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header[BearerPrefix.Length..].Trim()));
        string? principal = null;
        foreach (var (name, hash) in _keys)
        {
            // No early exit: the time taken does not depend on which key (if any) matched.
            if (CryptographicOperations.FixedTimeEquals(presented, hash))
            {
                principal = name;
            }
        }

        if (principal is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim("sub", principal), new Claim(ClaimTypes.NameIdentifier, principal)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
