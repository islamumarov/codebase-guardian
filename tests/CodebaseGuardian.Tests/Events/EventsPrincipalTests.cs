using System.Security.Claims;
using Mcp.Events;
using ModelContextProtocol.Protocol;

namespace CodebaseGuardian.Tests.Events;

public sealed class EventsPrincipalTests
{
    private static JsonRpcRequest RequestFor(ClaimsPrincipal user) =>
        new() { Method = "events/list", Context = new JsonRpcMessageContext { User = user } };

    private static ClaimsPrincipal Authenticated(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void The_sub_claim_is_the_principal() =>
        Assert.Equal("alice", EventsPrincipal.FromRequest(RequestFor(
            Authenticated(new Claim("sub", "alice"), new Claim(ClaimTypes.NameIdentifier, "other")))));

    [Fact]
    public void NameIdentifier_is_used_without_sub() =>
        Assert.Equal("bob", EventsPrincipal.FromRequest(RequestFor(Authenticated(new Claim(ClaimTypes.NameIdentifier, "bob")))));

    [Fact]
    public void The_identity_name_is_the_last_resort() =>
        Assert.Equal("carol", EventsPrincipal.FromRequest(RequestFor(Authenticated(new Claim(ClaimTypes.Name, "carol")))));

    [Fact]
    public void An_unauthenticated_identity_has_no_principal() =>
        Assert.Null(EventsPrincipal.FromRequest(RequestFor(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")])))));

    [Fact]
    public void A_request_without_context_has_no_principal() =>
        Assert.Null(EventsPrincipal.FromRequest(new JsonRpcRequest { Method = "events/list" }));

    [Fact]
    public void The_default_resolver_is_FromRequest() =>
        Assert.Equal(
            "alice",
            new EventsOptions().PrincipalResolver(RequestFor(Authenticated(new Claim("sub", "alice")))));
}
