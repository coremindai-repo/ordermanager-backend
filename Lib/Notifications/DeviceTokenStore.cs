using Dapper;

namespace OrderManager.Backend.Lib.Notifications;

public sealed record DeviceToken(Guid UserId, string Platform, string PushToken);

public interface IDeviceTokenStore
{
    Task<IReadOnlyList<DeviceToken>> GetForUsersAsync(IReadOnlyList<Guid> userIds);

    /// <summary>Removes a token Expo has reported as dead.</summary>
    Task DeleteAsync(string pushToken);
}

public sealed class SqlDeviceTokenStore(ISqlConnectionFactory connectionFactory) : IDeviceTokenStore
{
    public async Task<IReadOnlyList<DeviceToken>> GetForUsersAsync(IReadOnlyList<Guid> userIds)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        using var connection = connectionFactory.CreateConnection();

        return (await connection.QueryAsync<DeviceToken>(
            @"SELECT user_id AS UserId, platform AS Platform, push_token AS PushToken
              FROM device_tokens WHERE user_id IN @UserIds",
            new { UserIds = userIds })).ToList();
    }

    public async Task DeleteAsync(string pushToken)
    {
        using var connection = connectionFactory.CreateConnection();

        await connection.ExecuteAsync(
            "DELETE FROM device_tokens WHERE push_token = @PushToken", new { PushToken = pushToken });
    }
}
