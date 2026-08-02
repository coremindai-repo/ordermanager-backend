using OrderManager.Backend.Lib.Photos;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Blob paths arrive from the client on the confirmation call, after the bytes have
/// already gone straight to Blob storage. This validator is the control that stops a
/// caller attaching a blob that isn't theirs, so the rejection cases matter more than
/// the happy path.
/// </summary>
public class PhotoPathValidatorTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LineItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StepId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PhotoId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static string ValidPath() => $"{OrderId}/{LineItemId}/{StepId}/{PhotoId}.jpg";

    private static bool Check(string? path) => PhotoPathValidator.BelongsTo(path, OrderId, LineItemId, StepId);

    [Fact]
    public void Accepts_APathIssuedForThisStep()
    {
        Assert.True(Check(ValidPath()));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".heic")]
    [InlineData(".webp")]
    public void Accepts_EachAllowedExtension(string extension)
    {
        Assert.True(Check($"{OrderId}/{LineItemId}/{StepId}/{PhotoId}{extension}"));
    }

    // ---------- Ownership ----------

    [Fact]
    public void Rejects_APhotoBelongingToAnotherOrder()
    {
        var otherOrder = Guid.NewGuid();
        Assert.False(Check($"{otherOrder}/{LineItemId}/{StepId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_APhotoBelongingToAnotherLineItem()
    {
        var otherItem = Guid.NewGuid();
        Assert.False(Check($"{OrderId}/{otherItem}/{StepId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_APhotoBelongingToAnotherStep()
    {
        var otherStep = Guid.NewGuid();
        Assert.False(Check($"{OrderId}/{LineItemId}/{otherStep}/{PhotoId}.jpg"));
    }

    // ---------- Path shape ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_EmptyPaths(string? path)
    {
        Assert.False(Check(path));
    }

    [Fact]
    public void Rejects_TooFewSegments()
    {
        Assert.False(Check($"{OrderId}/{LineItemId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_TooManySegments()
    {
        Assert.False(Check($"{OrderId}/{LineItemId}/{StepId}/nested/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_NonGuidSegments()
    {
        Assert.False(Check($"{OrderId}/{LineItemId}/not-a-guid/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_FileNameThatIsNotAGuid()
    {
        Assert.False(Check($"{OrderId}/{LineItemId}/{StepId}/malicious.jpg"));
    }

    [Fact]
    public void Rejects_FileNameWithNoExtension()
    {
        Assert.False(Check($"{OrderId}/{LineItemId}/{StepId}/{PhotoId}"));
    }

    [Fact]
    public void Rejects_FileNameWithTrailingDot()
    {
        Assert.False(Check($"{OrderId}/{LineItemId}/{StepId}/{PhotoId}."));
    }

    // ---------- Traversal and absolute forms ----------

    [Fact]
    public void Rejects_ParentDirectoryTraversal()
    {
        Assert.False(Check($"{OrderId}/{LineItemId}/../{StepId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_BackslashSeparators()
    {
        Assert.False(Check($"{OrderId}\\{LineItemId}\\{StepId}\\{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_LeadingSlash()
    {
        Assert.False(Check($"/{OrderId}/{LineItemId}/{StepId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_AFullUrlRatherThanAPath()
    {
        // Guards against a client sending back the SAS URL instead of the blobPath.
        Assert.False(Check($"https://acct.blob.core.windows.net/production-photos/{OrderId}/{LineItemId}/{StepId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void Rejects_APathCarryingAQueryString()
    {
        // A SAS-bearing path must never be accepted for storage.
        Assert.False(Check($"{OrderId}/{LineItemId}/{StepId}/{PhotoId}.jpg?sv=2024-01-01&sig=abc"));
    }
}
