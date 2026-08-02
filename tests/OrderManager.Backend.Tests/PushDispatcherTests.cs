using Microsoft.Extensions.Logging.Abstractions;
using OrderManager.Backend.Lib.Notifications;

namespace OrderManager.Backend.Tests;

/// <summary>
/// The send-and-prune decisions, tested without a live Expo endpoint or a database.
/// These are the parts most likely to be wrong, because Expo reports per-token failures
/// inside a successful HTTP response.
/// </summary>
public class PushDispatcherTests
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private const string GoodToken = "ExponentPushToken[good]";
    private const string DeadToken = "ExponentPushToken[dead]";

    private static NotificationEvent Event() =>
        new("invoice_ready", "Order ready to invoice", "Order CUS-1 awaits invoicing", Guid.NewGuid());

    private sealed class FakeTokenStore(params DeviceToken[] tokens) : IDeviceTokenStore
    {
        private readonly List<DeviceToken> _tokens = [.. tokens];
        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<DeviceToken>> GetForUsersAsync(IReadOnlyList<Guid> userIds) =>
            Task.FromResult<IReadOnlyList<DeviceToken>>(
                _tokens.Where(t => userIds.Contains(t.UserId)).ToList());

        public Task DeleteAsync(string pushToken)
        {
            Deleted.Add(pushToken);
            _tokens.RemoveAll(t => t.PushToken == pushToken);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePushClient(Func<ExpoPushMessage, ExpoPushReceipt> respond) : IExpoPushClient
    {
        public List<ExpoPushMessage> Sent { get; } = [];

        public Task<IReadOnlyList<ExpoPushReceipt>> SendAsync(
            IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.FromResult<IReadOnlyList<ExpoPushReceipt>>(messages.Select(respond).ToList());
        }
    }

    private static PushDispatcher Build(IExpoPushClient client, IDeviceTokenStore store) =>
        new(client, store, NullLogger<PushDispatcher>.Instance);

    private static ExpoPushReceipt Ok(ExpoPushMessage m) => new(m.To, true, null, null);

    private static ExpoPushReceipt DeviceNotRegistered(ExpoPushMessage m) =>
        new(m.To, false, "DeviceNotRegistered", "\"ExponentPushToken[dead]\" is not a registered push notification recipient");

    // ---------- Successful send ----------

    [Fact]
    public async Task SuccessfulSend_ReportsDeliveryAndPrunesNothing()
    {
        var store = new FakeTokenStore(new DeviceToken(UserA, "ios", GoodToken));
        var client = new FakePushClient(Ok);

        var result = await Build(client, store).DispatchAsync([UserA], Event());

        Assert.Equal(1, result.Delivered);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.TokensPruned);
        Assert.Empty(store.Deleted);
        Assert.Equal([UserA], result.DeliveredToUserIds);
    }

    [Fact]
    public async Task SuccessfulSend_CarriesTheCustomFieldsUnderData()
    {
        // Contract §11: type/orderId/lineItemId travel under `data`, not at the top level.
        var store = new FakeTokenStore(new DeviceToken(UserA, "ios", GoodToken));
        var client = new FakePushClient(Ok);
        var notification = Event();

        await Build(client, store).DispatchAsync([UserA], notification);

        var sent = Assert.Single(client.Sent);
        Assert.Equal(GoodToken, sent.To);
        Assert.Equal(notification.Title, sent.Title);
        Assert.NotNull(sent.Data);

        var data = sent.Data!.GetType();
        Assert.Equal(notification.Type, data.GetProperty("type")!.GetValue(sent.Data));
        Assert.Equal(notification.OrderId, data.GetProperty("orderId")!.GetValue(sent.Data));
    }

    [Fact]
    public async Task NoRegisteredDevices_IsNotTreatedAsAFailure()
    {
        var store = new FakeTokenStore();
        var client = new FakePushClient(Ok);

        var result = await Build(client, store).DispatchAsync([UserA], Event());

        Assert.Equal(0, result.Delivered);
        Assert.Equal(0, result.Failed);
        Assert.Empty(client.Sent);
    }

    // ---------- DeviceNotRegistered ----------

    [Fact]
    public async Task DeviceNotRegistered_PrunesTheTokenOnTheFirstReport()
    {
        // Eager pruning by design: a device that comes back re-registers on next login.
        var store = new FakeTokenStore(new DeviceToken(UserA, "android", DeadToken));
        var client = new FakePushClient(DeviceNotRegistered);

        var result = await Build(client, store).DispatchAsync([UserA], Event());

        Assert.Equal(1, result.TokensPruned);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Delivered);
        Assert.Equal([DeadToken], store.Deleted);
    }

    [Fact]
    public async Task DeviceNotRegistered_StopsTheTokenBeingRetriedNextTime()
    {
        var store = new FakeTokenStore(new DeviceToken(UserA, "android", DeadToken));
        var dispatcher = Build(new FakePushClient(DeviceNotRegistered), store);

        await dispatcher.DispatchAsync([UserA], Event());

        // Second notification: the token is gone, so nothing is attempted.
        var secondClient = new FakePushClient(Ok);
        var second = await Build(secondClient, store).DispatchAsync([UserA], Event());

        Assert.Empty(secondClient.Sent);
        Assert.Equal(0, second.Failed);
    }

    // ---------- Mixed batch ----------

    [Fact]
    public async Task MixedBatch_DeadTokenDoesNotBlockDeliveryToTheGoodOne()
    {
        var store = new FakeTokenStore(
            new DeviceToken(UserA, "ios", GoodToken),
            new DeviceToken(UserB, "android", DeadToken));

        var client = new FakePushClient(m => m.To == DeadToken ? DeviceNotRegistered(m) : Ok(m));

        var result = await Build(client, store).DispatchAsync([UserA, UserB], Event());

        Assert.Equal(1, result.Delivered);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.TokensPruned);

        // The good user still got it, and only the dead token was removed.
        Assert.Equal([UserA], result.DeliveredToUserIds);
        Assert.Equal([DeadToken], store.Deleted);
    }

    [Fact]
    public async Task MixedBatch_BothMessagesAreStillAttempted()
    {
        var store = new FakeTokenStore(
            new DeviceToken(UserA, "ios", GoodToken),
            new DeviceToken(UserB, "android", DeadToken));

        var client = new FakePushClient(m => m.To == DeadToken ? DeviceNotRegistered(m) : Ok(m));

        await Build(client, store).DispatchAsync([UserA, UserB], Event());

        // A dead token must not short-circuit the batch.
        Assert.Equal(2, client.Sent.Count);
    }

    [Fact]
    public async Task MixedBatch_OrderOfDeadAndGoodDoesNotMatter()
    {
        // Expo returns results positionally; a dead token first must not misattribute
        // the failure to the wrong device.
        var store = new FakeTokenStore(
            new DeviceToken(UserB, "android", DeadToken),
            new DeviceToken(UserA, "ios", GoodToken));

        var client = new FakePushClient(m => m.To == DeadToken ? DeviceNotRegistered(m) : Ok(m));

        var result = await Build(client, store).DispatchAsync([UserA, UserB], Event());

        Assert.Equal([UserA], result.DeliveredToUserIds);
        Assert.Equal([DeadToken], store.Deleted);
    }

    // ---------- Other failures ----------

    [Fact]
    public async Task CredentialErrors_AreNotTreatedAsDeadTokens()
    {
        // MismatchSenderId means every push to that platform fails — pruning the token
        // would delete healthy registrations across the whole user base.
        var store = new FakeTokenStore(new DeviceToken(UserA, "android", GoodToken));
        var client = new FakePushClient(m => new ExpoPushReceipt(m.To, false, "MismatchSenderId", "bad credentials"));

        var result = await Build(client, store).DispatchAsync([UserA], Event());

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.TokensPruned);
        Assert.Empty(store.Deleted);
    }

    [Fact]
    public async Task UnknownErrors_FailWithoutPruning()
    {
        var store = new FakeTokenStore(new DeviceToken(UserA, "ios", GoodToken));
        var client = new FakePushClient(m => new ExpoPushReceipt(m.To, false, "MessageRateExceeded", "slow down"));

        var result = await Build(client, store).DispatchAsync([UserA], Event());

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.TokensPruned);
    }

    [Fact]
    public async Task AUserWithSeveralDevices_GetsCountedOnce()
    {
        var store = new FakeTokenStore(
            new DeviceToken(UserA, "ios", "ExponentPushToken[phone]"),
            new DeviceToken(UserA, "android", "ExponentPushToken[tablet]"));

        var result = await Build(new FakePushClient(Ok), store).DispatchAsync([UserA], Event());

        Assert.Equal(2, result.Delivered);
        Assert.Equal([UserA], result.DeliveredToUserIds);
    }
}
