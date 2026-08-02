using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

public class AuthLogin(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    public record LoginRequest(string Username, string Password);

    private record UserRecord(Guid Id, string PasswordHash, string FirstName, string LastName, string? MobileNo, bool Active);

    [Function("AuthLogin")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")] HttpRequest req)
    {
        var body = await req.ReadFromJsonAsync<LoginRequest>();
        if (string.IsNullOrWhiteSpace(body?.Username) || string.IsNullOrWhiteSpace(body.Password))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "username and password are required");
        }

        using var connection = connectionFactory.CreateConnection();

        var user = await connection.QuerySingleOrDefaultAsync<UserRecord>(
            @"SELECT id AS Id, password_hash AS PasswordHash, first_name AS FirstName,
                     last_name AS LastName, mobile_no AS MobileNo, active AS Active
              FROM users WHERE username = @Username",
            new { body.Username });

        if (user is null || !user.Active || !PasswordHasher.Verify(body.Password, user.PasswordHash))
        {
            throw new AppException(StatusCodes.Status401Unauthorized, "INVALID_CREDENTIALS", "Invalid username or password");
        }

        var roles = (await connection.QueryAsync<string>(
            "SELECT role FROM user_roles WHERE user_id = @UserId", new { UserId = user.Id })).ToList();

        var (token, expiresAt) = jwtService.IssueToken(user.Id, roles);

        return new OkObjectResult(new
        {
            token,
            expiresAt = expiresAt.ToString("o"),
            user = new
            {
                userId = user.Id,
                firstName = user.FirstName,
                lastName = user.LastName,
                mobileNo = user.MobileNo,
                roles,
            },
        });
    }
}
