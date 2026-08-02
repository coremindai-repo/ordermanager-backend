using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace OrderManager.Backend.Lib;

public record TokenResult(string Token, DateTime ExpiresAt);

// HS256, 12h expiry, per API-INTERFACE-CONTRACT.md §2
public class JwtService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
    private readonly string _secret;

    public JwtService(IConfiguration configuration)
    {
        _secret = configuration["JWT_SECRET"]
            ?? throw new InvalidOperationException("JWT_SECRET is not configured");
    }

    public TokenResult IssueToken(Guid userId, IEnumerable<string> roles)
    {
        var expiresAt = DateTime.UtcNow.Add(TokenLifetime);

        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId.ToString()) };
        claims.AddRange(roles.Select(role => new Claim("roles", role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(claims: claims, expires: expiresAt, signingCredentials: credentials);
        return new TokenResult(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public ClaimsPrincipal ValidateToken(string token)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_secret));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            IssuerSigningKey = key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        };
        return handler.ValidateToken(token, parameters, out _);
    }
}
