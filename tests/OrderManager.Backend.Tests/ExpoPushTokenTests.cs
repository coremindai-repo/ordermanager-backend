using OrderManager.Backend.Lib.Notifications;

namespace OrderManager.Backend.Tests;

/// <summary>
/// The backend only ever handles Expo push tokens — Expo brokers delivery to FCM/APNs,
/// so no raw platform token should reach this system. Catching them matters because
/// Expo reports bad tokens inside a 200 response rather than as an HTTP error, so one
/// slipping through looks like a successful send.
/// </summary>
public class ExpoPushTokenTests
{
    [Theory]
    [InlineData("ExponentPushToken[xxxxxxxxxxxxxxxxxxxxxx]")]
    [InlineData("ExponentPushToken[a]")]
    [InlineData("ExpoPushToken[legacyformat]")]
    public void AcceptsExpoTokens(string token)
    {
        Assert.True(ExpoPushToken.IsValid(token));
    }

    [Fact]
    public void AcceptsTokensWithSurroundingWhitespace()
    {
        Assert.True(ExpoPushToken.IsValid("  ExponentPushToken[abc]  "));
    }

    [Theory]
    [InlineData("fcm:APA91bHun4MxP5egoKMoifhewkjfhwef")]
    [InlineData("dGhpcyBpcyBhbiBBUE5zIHRva2Vu")]
    [InlineData("00fc13adff785122b4ad28809a3420982341241421348097878e577c991de8f0")]
    public void RejectsRawPlatformTokens(string token)
    {
        // The case that matters: a bare FCM or APNs token means the app is not
        // registering through Expo's notification API.
        Assert.False(ExpoPushToken.IsValid(token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsEmptyTokens(string? token)
    {
        Assert.False(ExpoPushToken.IsValid(token));
    }

    [Theory]
    [InlineData("ExponentPushToken[")]          // unterminated
    [InlineData("ExponentPushToken[]")]         // nothing inside
    [InlineData("ExponentPushTokenabc]")]       // no opening bracket
    [InlineData("NotAPushToken[abc]")]          // wrong prefix
    [InlineData("exponentpushtoken[abc]")]      // Expo's casing is exact
    public void RejectsMalformedTokens(string token)
    {
        Assert.False(ExpoPushToken.IsValid(token));
    }

    [Fact]
    public void RejectsAPrefixThatMerelyContainsTheExpoName()
    {
        Assert.False(ExpoPushToken.IsValid("MyExponentPushToken[abc]"));
    }
}
