using System.Security.Claims;
using ModelContextProtocol.Protocol;

namespace Mcp.Events;

/// <summary>Resolves the authenticated caller of a request.</summary>
public static class EventsPrincipal
{
    /// <summary>
    /// The authenticated user's <c>sub</c> claim, else <see cref="ClaimTypes.NameIdentifier"/>, else the identity name;
    /// <see langword="null"/> when the request carries no authenticated user.
    /// </summary>
    public static string? FromRequest(JsonRpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var user = request.Context?.User;
        if (user?.Identity is not { IsAuthenticated: true } identity)
        {
            return null;
        }

        return user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? identity.Name;
    }
}
