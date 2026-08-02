using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Tests;

public class TransitionOutcomeMapperTests
{
    [Theory]
    [InlineData(TransitionOutcome.UnknownTargetStatus, 400, "VALIDATION_ERROR")]
    [InlineData(TransitionOutcome.RoleNotPermitted, 403, "FORBIDDEN")]
    [InlineData(TransitionOutcome.UnknownCurrentStatus, 409, "ILLEGAL_TRANSITION")]
    [InlineData(TransitionOutcome.TransitionNotAllowed, 409, "ILLEGAL_TRANSITION")]
    [InlineData(TransitionOutcome.MethodNotPermitted, 409, "ILLEGAL_TRANSITION")]
    public void MapsDenialsToContractErrorCodes(TransitionOutcome outcome, int expectedStatus, string expectedCode)
    {
        var exception = TransitionOutcomeMapper.ToException(new TransitionDecision(outcome, "because"));

        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal("because", exception.Message);
    }

    [Fact]
    public void IllegalTransitionsUse409_AsRequiredByTheContract()
    {
        // CLAUDE.md §5 and contract §12 both pin illegal transitions to 409 specifically.
        var illegal = new[]
        {
            TransitionOutcome.TransitionNotAllowed,
            TransitionOutcome.UnknownCurrentStatus,
            TransitionOutcome.MethodNotPermitted,
        };

        Assert.All(illegal, outcome =>
            Assert.Equal(409, TransitionOutcomeMapper.ToException(new TransitionDecision(outcome, "x")).StatusCode));
    }

    [Fact]
    public void Throws_WhenAskedToMapAnAllowedDecision()
    {
        // Guards against a caller accidentally treating success as a failure path.
        var rule = new TransitionRule { From = "A", To = "B" };

        Assert.Throws<InvalidOperationException>(() =>
            TransitionOutcomeMapper.ToException(TransitionDecision.Allow(rule)));
    }
}

public class WorkflowTemplateTests
{
    private static readonly WorkflowTemplate Template = WorkflowTemplate.Parse("""
    {
      "initialStatus": "NEW",
      "statuses": [
        { "code": "NEW", "name": "New" },
        { "code": "IN_PRODUCTION", "name": "In Production" }
      ],
      "transitions": [ { "from": "NEW", "to": "IN_PRODUCTION" } ]
    }
    """);

    [Theory]
    [InlineData("in_production")]
    [InlineData("IN_PRODUCTION")]
    [InlineData("In_Production")]
    public void ResolveStatusCode_ReturnsTemplateCasing_WhateverTheCallerSent(string supplied)
    {
        // Whatever casing the mobile app sends, the stored status is the template's.
        Assert.Equal("IN_PRODUCTION", Template.ResolveStatusCode(supplied));
    }

    [Fact]
    public void Parse_RejectsTemplateWithNoStatuses()
    {
        Assert.Throws<InvalidOperationException>(() => WorkflowTemplate.Parse("""
        { "initialStatus": "X", "statuses": [], "transitions": [] }
        """));
    }
}
