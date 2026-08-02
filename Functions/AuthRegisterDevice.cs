using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;

namespace OrderManager.Backend.Functions;

public class AuthRegisterDevice(ISqlConnectionFactory connectionFactory, JwtService jwtService)
{
    public record RegisterDeviceRequest(string Platform, string PushToken);

    [Function("AuthRegisterDevice")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register-device")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        var body = await req.ReadFromJsonAsync<RegisterDeviceRequest>();
        if (body?.Platform is not ("ios" or "android"))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "platform must be \"ios\" or \"android\"");
        }
        if (string.IsNullOrWhiteSpace(body.PushToken))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "pushToken is required");
        }

        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            @"MERGE device_tokens AS target
              USING (SELECT @UserId AS user_id, @Platform AS platform) AS source
              ON target.user_id = source.user_id AND target.platform = source.platform
              WHEN MATCHED THEN
                UPDATE SET push_token = @PushToken, updated_at = SYSUTCDATETIME()
              WHEN NOT MATCHED THEN
                INSERT (user_id, platform, push_token, updated_at)
                VALUES (@UserId, @Platform, @PushToken, SYSUTCDATETIME());",
            new { UserId = caller.UserId, body.Platform, body.PushToken });

        return new OkObjectResult(new { success = true });
    }
}
