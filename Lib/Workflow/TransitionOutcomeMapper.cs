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

        // OrderTypeNotPermitted is a plain illegal transition, not an actionable one:
        // unlike a missing store, there is nothing the user can do — a stock order is
        // never going to be invoiced.
        TransitionOutcome.UnknownCurrentStatus or
        TransitionOutcome.TransitionNotAllowed or
        TransitionOutcome.MethodNotPermitted or
        TransitionOutcome.OrderTypeNotPermitted =>
            new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION", decision.Message),

        // Distinct code: the move is legal in principle, the order just isn't ready —
        // the mobile app should say "finish the remaining items", not "not allowed".
        TransitionOutcome.LineItemsIncomplete =>
            new AppException(StatusCodes.Status409Conflict, "LINE_ITEMS_INCOMPLETE", decision.Message),

        // Also actionable rather than forbidden: the app should send the user to the
        // store picker, not tell them the move is illegal.
        TransitionOutcome.DestinationStoreRequired =>
            new AppException(StatusCodes.Status409Conflict, "DESTINATION_STORE_REQUIRED", decision.Message),

        _ => throw new InvalidOperationException(
            $"Cannot map outcome '{decision.Outcome}' to an error — it is not a denial"),
    };
}
