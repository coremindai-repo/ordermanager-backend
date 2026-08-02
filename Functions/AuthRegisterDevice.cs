using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Notifications;

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

        // Rejected here rather than at the database constraint so the caller gets a
        // message explaining the problem. A bare FCM/APNs token means the app is not
        // registering through Expo's notification API — and it would fail at Expo's end
        // per-token inside a 200 response, which is easy to miss.
        if (!ExpoPushToken.IsValid(body.PushToken))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "pushToken must be an Expo push token, e.g. ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]. " +
                "Raw FCM/APNs tokens are not accepted — the app registers for push through Expo.");
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
