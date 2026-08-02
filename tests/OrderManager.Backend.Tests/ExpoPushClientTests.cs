using System.Net;
using System.Text;
using OrderManager.Backend.Lib.Notifications;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Parsing of Expo's actual response shape. The point of these is the thing the client
/// exists to prevent: a 200 from Expo does not mean delivered, because per-token
/// failures arrive inside the body.
/// </summary>
public class ExpoPushClientTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? CapturedRequestBody { get; private set; }
        public Uri? CapturedUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            CapturedRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (ExpoPushClient Client, StubHandler Handler) Build(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        return (new ExpoPushClient(new HttpClient(handler)), handler);
    }

    private static ExpoPushMessage Message(string token = "ExponentPushToken[abc]") =>
        new(token, "Title", "Body", new { type = "invoice_ready" });

    [Fact]
    public async Task ParsesASuccessfulTicket()
    {
        var (client, _) = Build(HttpStatusCode.OK,
            """{"data":[{"status":"ok","id":"XXXX-XXXX-XXXX"}]}""");

        var receipt = Assert.Single(await client.SendAsync([Message()]));

        Assert.True(receipt.Ok);
        Assert.Null(receipt.ErrorCode);
    }

    [Fact]
    public async Task ParsesADeviceNotRegisteredErrorFromInsideA200()
    {
        // The whole reason the body must be read.
        var (client, _) = Build(HttpStatusCode.OK, """
        {"data":[{"status":"error","message":"\"ExponentPushToken[abc]\" is not a registered push notification recipient","details":{"error":"DeviceNotRegistered"}}]}
        """);

        var receipt = Assert.Single(await client.SendAsync([Message()]));

        Assert.False(receipt.Ok);
        Assert.True(receipt.IsDeviceNotRegistered);
        Assert.Contains("not a registered", receipt.Message);
    }

    [Fact]
    public async Task ParsesAMixedBatchAndKeepsTokensAlignedWithResults()
    {
        // Expo returns results positionally, so alignment is what ties a failure to the
        // right token. Getting this wrong would prune the healthy device.
        var (client, _) = Build(HttpStatusCode.OK, """
        {"data":[
          {"status":"ok","id":"1"},
          {"status":"error","message":"gone","details":{"error":"DeviceNotRegistered"}},
          {"status":"ok","id":"3"}
        ]}
        """);

        var receipts = await client.SendAsync([
            Message("ExponentPushToken[first]"),
            Message("ExponentPushToken[second]"),
            Message("ExponentPushToken[third]"),
        ]);

        Assert.Equal(3, receipts.Count);
        Assert.True(receipts[0].Ok);
        Assert.False(receipts[1].Ok);
        Assert.Equal("ExponentPushToken[second]", receipts[1].PushToken);
        Assert.True(receipts[1].IsDeviceNotRegistered);
        Assert.True(receipts[2].Ok);
    }

    [Fact]
    public async Task UsesTheTokenExpoEchoesBackRatherThanTrustingPosition()
    {
        // Verified against the live Expo API: errors carry details.expoPushToken. That
        // is authoritative, so even a response returned out of order attributes the
        // failure to the right device — mis-attributing would prune a healthy token.
        var (client, _) = Build(HttpStatusCode.OK, """
        {"data":[
          {"status":"error","message":"gone","details":{"error":"DeviceNotRegistered","expoPushToken":"ExponentPushToken[second]"}},
          {"status":"ok","id":"1"}
        ]}
        """);

        var receipts = await client.SendAsync([
            Message("ExponentPushToken[first]"),
            Message("ExponentPushToken[second]"),
        ]);

        // Position 0 held "first", but Expo says the failure was "second".
        Assert.False(receipts[0].Ok);
        Assert.Equal("ExponentPushToken[second]", receipts[0].PushToken);
    }

    [Fact]
    public async Task ParsesTheExactShapeTheLiveExpoApiReturns()
    {
        // Captured verbatim from https://exp.host/--/api/v2/push/send with an
        // unregistered token, so this test fails if Expo changes its contract.
        var (client, _) = Build(HttpStatusCode.OK, """
        {"data":[{"status":"error","message":"\"ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]\" is not a registered push notification recipient or it is associated with a project that does not exist.","details":{"error":"DeviceNotRegistered","expoPushToken":"ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]"}}]}
        """);

        var receipt = Assert.Single(await client.SendAsync([Message("ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]")]));

        Assert.False(receipt.Ok);
        Assert.True(receipt.IsDeviceNotRegistered);
        Assert.Equal("ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]", receipt.PushToken);
    }

    [Fact]
    public async Task IdentifiesCredentialProblemsSeparatelyFromDeadTokens()
    {
        var (client, _) = Build(HttpStatusCode.OK, """
        {"data":[{"status":"error","message":"bad key","details":{"error":"MismatchSenderId"}}]}
        """);

        var receipt = Assert.Single(await client.SendAsync([Message()]));

        Assert.True(receipt.IsCredentialProblem);
        Assert.False(receipt.IsDeviceNotRegistered);
    }

    [Fact]
    public async Task AFewerResultsThanMessagesResponseDoesNotMisalign()
    {
        // Defensive: a truncated response must not silently mark later tokens as ok.
        var (client, _) = Build(HttpStatusCode.OK, """{"data":[{"status":"ok","id":"1"}]}""");

        var receipts = await client.SendAsync([
            Message("ExponentPushToken[first]"),
            Message("ExponentPushToken[second]"),
        ]);

        Assert.Equal(2, receipts.Count);
        Assert.True(receipts[0].Ok);
        Assert.False(receipts[1].Ok);
        Assert.Equal("NoReceipt", receipts[1].ErrorCode);
    }

    [Fact]
    public async Task ThrowsOnANonSuccessStatus()
    {
        // A non-200 is a whole-batch failure — nothing was delivered, so it must not be
        // mistaken for per-token results.
        var (client, _) = Build(HttpStatusCode.BadRequest, """{"errors":[{"code":"BAD_REQUEST"}]}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync([Message()]));
    }

    [Fact]
    public async Task PostsToTheExpoSendEndpoint()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"data":[{"status":"ok"}]}""");

        await client.SendAsync([Message()]);

        Assert.Equal(ExpoPushClient.SendUrl, handler.CapturedUri!.ToString());
    }

    [Fact]
    public async Task SendsTheTokenAsToAndNestsCustomFieldsUnderData()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"data":[{"status":"ok"}]}""");

        await client.SendAsync([Message("ExponentPushToken[xyz]")]);

        Assert.Contains("\"to\":\"ExponentPushToken[xyz]\"", handler.CapturedRequestBody);
        Assert.Contains("\"data\":", handler.CapturedRequestBody);
    }

    [Fact]
    public async Task SendingNothingMakesNoRequest()
    {
        var (client, handler) = Build(HttpStatusCode.OK, """{"data":[]}""");

        Assert.Empty(await client.SendAsync([]));
        Assert.Null(handler.CapturedUri);
    }
}
