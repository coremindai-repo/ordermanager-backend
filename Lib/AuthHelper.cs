using Microsoft.AspNetCore.Http;

namespace OrderManager.Backend.Lib;

public static class AuthHelper
{
    public static Guid RequireUserId(HttpRequest request, JwtService jwtService)
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
            var sub = principal.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
            if (sub is null || !Guid.TryParse(sub, out var userId))
            {
                throw new AppException(StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "Invalid token");
            }
            return userId;
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
