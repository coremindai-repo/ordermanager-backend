using Microsoft.AspNetCore.Http;

namespace OrderManager.Backend.Lib.Workflow;

public static class TransitionOutcomeMapper
{
    /// <summary>
    /// Maps a denied decision onto the error codes in API-INTERFACE-CONTRACT.md §12.
    /// An unknown *target* status is a client-side mistake (400); an illegal move —
    /// including one blocked by the item's method, or an entity sitting in a status the
    /// active template no longer defines — is 409; a role block is 403.
    /// </summary>
    public static AppException ToException(TransitionDecision decision) => decision.Outcome switch
    {
        TransitionOutcome.UnknownTargetStatus =>
            new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", decision.Message),

        TransitionOutcome.RoleNotPermitted =>
            new AppException(StatusCodes.Status403Forbidden, "FORBIDDEN", decision.Message),

        TransitionOutcome.UnknownCurrentStatus or
        TransitionOutcome.TransitionNotAllowed or
        TransitionOutcome.MethodNotPermitted =>
            new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION", decision.Message),

        _ => throw new InvalidOperationException(
            $"Cannot map outcome '{decision.Outcome}' to an error — it is not a denial"),
    };
}
