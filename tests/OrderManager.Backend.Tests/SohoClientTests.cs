using Microsoft.Extensions.Logging.Abstractions;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Soho;

namespace OrderManager.Backend.Tests;

public class SohoClientTests
{
    private static SohoDraftOrderRequest SampleRequest() =>
        new(Guid.NewGuid(), Guid.NewGuid(), [new SohoLineItem("Test Chair", "Oak")]);

    // ---------- Stub ----------

    private static StubSohoClient Stub() => new(NullLogger<StubSohoClient>.Instance);

    [Fact]
    public async Task Stub_ReturnsAVisiblyFakeReference()
    {
        // The placeholder must not be mistakable for a real SOHO number months later.
        var result = await Stub().CreateDraftSalesOrderAsync(SampleRequest());

        Assert.StartsWith("STUB", result);
    }

    [Fact]
    public async Task Stub_ProducesAnOrderNumberThatIsObviouslyPlaceholder()
    {
        var soho = await Stub().CreateDraftSalesOrderAsync(SampleRequest());

        Assert.StartsWith("CUS-STUB", OrderNumberFormatter.ForCustomerOrder(soho));
    }

    [Fact]
    public async Task Stub_CancelIsANoOp()
    {
        // The compensation path must be safely callable even though nothing was created.
        await Stub().CancelDraftSalesOrderAsync("STUB123456");
    }

    // ---------- Unconfigured (the default) ----------

    [Fact]
    public async Task Unconfigured_FailsCustomerSubmissionWith503()
    {
        // CLAUDE.md §8: fail cleanly rather than create an order with no valid reference.
        var exception = await Assert.ThrowsAsync<AppException>(
            () => new UnconfiguredSohoClient().CreateDraftSalesOrderAsync(SampleRequest()));

        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("SOHO_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public async Task Unconfigured_CancelDoesNotThrow()
    {
        // Compensation runs on the failure path; it must never mask the original error.
        await new UnconfiguredSohoClient().CancelDraftSalesOrderAsync("anything");
    }

    [Fact]
    public async Task Unconfigured_ErrorMessageExplainsHowToEnableTheStub()
    {
        var exception = await Assert.ThrowsAsync<AppException>(
            () => new UnconfiguredSohoClient().CreateDraftSalesOrderAsync(SampleRequest()));

        Assert.Contains("SOHO_MODE=stub", exception.Message);
    }
}
