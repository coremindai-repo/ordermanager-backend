using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;

namespace OrderManager.Backend.Lib;

/// <summary>The authenticated caller, as asserted by their JWT.</summary>
public sealed record Caller(Guid UserId, IReadOnlyList<string> Roles);

public static class AuthHelper
{
    /// <summary>
    /// Resolves the caller from the Bearer token, or throws a 401 AppException.
    /// Roles come from the token so no extra DB round-trip is needed per request;
    /// tokens are short-lived (12h) so a role change takes effect at next login.
    /// </summary>
    public static Caller RequireCaller(HttpRequest request, JwtService jwtService)
    {
        if (!request.Headers.TryGetValue("Authorization", out var header) ||
            !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Missing or invalid Authorization header");
        }

        var token = header.ToString()["Bearer ".Length..];

        try
        {
            var principal = jwtService.ValidateToken(token);

            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (sub is null || !Guid.TryParse(sub, out var userId))
            {
                throw new AppException(StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid token");
            }

            var roles = principal.FindAll("roles").Select(c => c.Value).ToList();
            return new Caller(userId, roles);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception)
        {
            // Malformed tokens can throw ArgumentException, SecurityTokenException, etc. — all mean "not a usable token".
            throw new AppException(StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid or expired token");
        }
    }
}
