using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Photos;

namespace OrderManager.Backend.Tests;

/// <summary>
/// Reference photos live at {orderId}/{lineItemId}/reference/{guid}.ext — four segments
/// like step photos, but with a literal segment where the step id would be.
///
/// The two validators are separate on purpose: widening the step validator to accept a
/// non-GUID third segment would weaken the check guarding step photos in order to serve
/// a different case. These tests hold that separation.
/// </summary>
public class ReferencePhotoPathTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LineItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid StepId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PhotoId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static string ReferencePath() => $"{OrderId}/{LineItemId}/reference/{PhotoId}.jpg";

    private static bool CheckReference(string? path) =>
        PhotoPathValidator.BelongsToLineItemReference(path, OrderId, LineItemId);

    [Fact]
    public void AcceptsAReferencePathForThisLineItem()
    {
        Assert.True(CheckReference(ReferencePath()));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".heic")]
    public void AcceptsEachAllowedExtension(string extension)
    {
        Assert.True(CheckReference($"{OrderId}/{LineItemId}/reference/{PhotoId}{extension}"));
    }

    // ---------- Ownership ----------

    [Fact]
    public void RejectsAnotherOrdersReferencePhoto()
    {
        Assert.False(CheckReference($"{Guid.NewGuid()}/{LineItemId}/reference/{PhotoId}.jpg"));
    }

    [Fact]
    public void RejectsAnotherLineItemsReferencePhoto()
    {
        Assert.False(CheckReference($"{OrderId}/{Guid.NewGuid()}/reference/{PhotoId}.jpg"));
    }

    // ---------- The two path kinds must not cross ----------

    [Fact]
    public void AStepPhotoPathIsNotAValidReferencePath()
    {
        // Otherwise a step photo could be attached as a reference photo.
        Assert.False(CheckReference($"{OrderId}/{LineItemId}/{StepId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void AReferencePathIsNotAValidStepPath()
    {
        // And the reverse — the step validator must still demand a real step id.
        Assert.False(PhotoPathValidator.BelongsTo(ReferencePath(), OrderId, LineItemId, StepId));
    }

    [Fact]
    public void TheReferenceSegmentCannotBeMistakenForAStepId()
    {
        // "reference" is not parseable as a GUID, which is what guarantees a real step
        // folder can never collide with the reference folder.
        Assert.False(Guid.TryParse(PhotoPathValidator.ReferenceSegment, out _));
    }

    // ---------- Shape ----------

    [Theory]
    [InlineData("Reference")]
    [InlineData("REFERENCE")]
    [InlineData("references")]
    public void RejectsAnythingButTheExactSegment(string segment)
    {
        Assert.False(CheckReference($"{OrderId}/{LineItemId}/{segment}/{PhotoId}.jpg"));
    }

    [Fact]
    public void RejectsAMissingSegment()
    {
        Assert.False(CheckReference($"{OrderId}/{LineItemId}/{PhotoId}.jpg"));
    }

    [Fact]
    public void RejectsANonGuidFileName()
    {
        Assert.False(CheckReference($"{OrderId}/{LineItemId}/reference/malicious.jpg"));
    }

    [Fact]
    public void RejectsADisallowedExtension()
    {
        Assert.False(CheckReference($"{OrderId}/{LineItemId}/reference/{PhotoId}.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsEmptyPaths(string? path)
    {
        Assert.False(CheckReference(path));
    }

    [Fact]
    public void RejectsTraversalAndUrlForms()
    {
        Assert.False(CheckReference($"{OrderId}/../{LineItemId}/reference/{PhotoId}.jpg"));
        Assert.False(CheckReference($"/{OrderId}/{LineItemId}/reference/{PhotoId}.jpg"));
        Assert.False(CheckReference($"https://acct.blob.core.windows.net/{OrderId}/{LineItemId}/reference/{PhotoId}.jpg"));
        Assert.False(CheckReference($"{OrderId}/{LineItemId}/reference/{PhotoId}.jpg?sv=2024&sig=abc"));
    }
}

/// <summary>
/// Photo endpoints are now gated by the same order-visibility rule the list endpoints
/// use, rather than being open to any authenticated caller.
/// </summary>
public class OrderAccessScopeTests
{
    private static readonly Guid Owner = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void ASalespersonMayActOnTheirOwnOrder()
    {
        Assert.True(AccessScope.CanAccessOrder(["salesperson"], Owner, Owner));
    }

    [Fact]
    public void ASalespersonMayNotActOnSomeoneElsesOrder()
    {
        Assert.False(AccessScope.CanAccessOrder(["salesperson"], Other, Owner));
    }

    [Theory]
    [InlineData("factory_supervisor")]
    [InlineData("store_manager")]
    [InlineData("company_manager")]
    public void RolesThatSeeAllOrdersMayActOnAnyOrder(string role)
    {
        Assert.True(AccessScope.CanAccessOrder([role], Other, Owner));
    }

    [Fact]
    public void ACallerWithNoRolesMayStillActOnTheirOwnOrder()
    {
        // Fail closed on other people's orders, but do not lock someone out of their own.
        Assert.True(AccessScope.CanAccessOrder([], Owner, Owner));
        Assert.False(AccessScope.CanAccessOrder([], Other, Owner));
    }
}
