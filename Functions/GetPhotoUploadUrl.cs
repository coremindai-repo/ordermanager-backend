using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Photos;

namespace OrderManager.Backend.Functions;

/// <summary>
/// POST /api/order-line-items/{lineItemId}/production-steps/{stepId}/photo-upload-url
///
/// Issues a short-lived write-only SAS for a single new blob. The device uploads
/// straight to Blob storage, then sends the returned blobPath back on the step update
/// call, where the upload is validated.
///
/// Documented in API-INTERFACE-CONTRACT.md §5.
/// </summary>
public class GetPhotoUploadUrl(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    IPhotoStorage photoStorage)
{
    public record UploadUrlRequest(string? FileExtension);

    private record StepRow(Guid Id, Guid LineItemId, Guid OrderId, Guid OrderCreatedBy);

    [Function("GetPhotoUploadUrl")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "order-line-items/{lineItemId}/production-steps/{stepId}/photo-upload-url")] HttpRequest req,
        string lineItemId,
        string stepId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        // Step photos are evidence of factory work, so only the role that performs step
        // transitions may attach them — matching the template's gating on the step
        // updates themselves. Previously this endpoint was open to any authenticated
        // caller, which was the one action in the system left ungated.
        AuthHelper.RequireRole(caller, "factory_supervisor");

        if (!Guid.TryParse(lineItemId, out var itemId) || !Guid.TryParse(stepId, out var parsedStepId))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "lineItemId and stepId must be GUIDs");
        }

        var body = await req.ReadFromJsonAsync<UploadUrlRequest>();
        var extension = string.IsNullOrWhiteSpace(body?.FileExtension) ? ".jpg" : body.FileExtension;

        using var connection = connectionFactory.CreateConnection();

        // Resolve the order from the step itself rather than trusting the caller, so the
        // blob path can never be pointed at another order's folder.
        var step = await connection.QuerySingleOrDefaultAsync<StepRow>(
            @"SELECT s.id AS Id, s.line_item_id AS LineItemId, li.order_id AS OrderId,
                     o.created_by AS OrderCreatedBy
              FROM order_line_item_steps s
              JOIN order_line_items li ON li.id = s.line_item_id
              JOIN orders o ON o.id = li.order_id
              WHERE s.id = @StepId AND s.line_item_id = @LineItemId",
            new { StepId = parsedStepId, LineItemId = itemId });

        if (step is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND",
                $"Step {parsedStepId} not found on line item {itemId}");
        }

        // Same order-visibility rule the list endpoints apply, reused rather than
        // reinvented so "which orders can I touch" cannot drift from "which can I see".
        if (!AccessScope.CanAccessOrder(caller.Roles, caller.UserId, step.OrderCreatedBy))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "FORBIDDEN",
                "You may only attach photos to orders you can access");
        }

        var target = photoStorage.CreateUploadTarget(step.OrderId, step.LineItemId, step.Id, extension);

        return new OkObjectResult(new
        {
            uploadUrl = target.UploadUrl,
            blobPath = target.BlobPath,
            expiresAt = target.ExpiresAt.UtcDateTime.ToString("o"),
            // The device must send this header when PUTting to the SAS URL.
            requiredHeaders = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" },
        });
    }
}
