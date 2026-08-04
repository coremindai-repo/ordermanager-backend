using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Photos;

namespace OrderManager.Backend.Functions;

/// <summary>
/// POST /api/order-line-items/{lineItemId}/production-steps/{stepId}/update (contract §5).
///
/// This is also the confirmation point for photo uploads: because the bytes went
/// straight to Blob storage without passing through the Function, the blob paths
/// submitted here are validated now — ownership, existence, content type and size.
/// </summary>
public class UpdateProductionStep(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    IPhotoStorage photoStorage)
{
    public record StepUpdateRequest(string Status, List<string>? AssignedNames, List<string>? PhotoUrls);

    private record StepRow(Guid Id, Guid LineItemId, Guid OrderId, string Status, string? PhotoUrls);

    private static readonly string[] ValidStatuses = ["started", "complete"];

    /// <summary>Generous for a phone photo, low enough to refuse a video or a mis-picked file.</summary>
    private const long MaxPhotoBytes = 15 * 1024 * 1024;

    [Function("UpdateProductionStep")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "order-line-items/{lineItemId}/production-steps/{stepId}/update")] HttpRequest req,
        string lineItemId,
        string stepId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        // Matches the SAS-issuing endpoint's gate (GetPhotoUploadUrl) and CLAUDE.md §5's
        // role table: production step transitions are factory_supervisor-only. This
        // endpoint is what actually writes the step status and photos, so it needs the
        // same gate, not just the SAS step ahead of it.
        AuthHelper.RequireRole(caller, "factory_supervisor");

        if (!Guid.TryParse(lineItemId, out var itemId) || !Guid.TryParse(stepId, out var parsedStepId))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "lineItemId and stepId must be GUIDs");
        }

        var body = await req.ReadFromJsonAsync<StepUpdateRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Status) ||
            !ValidStatuses.Contains(body.Status, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"status must be one of: {string.Join(", ", ValidStatuses)}");
        }

        var targetStatus = body.Status.ToLowerInvariant();

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        var step = await connection.QuerySingleOrDefaultAsync<StepRow>(
            @"SELECT s.id AS Id, s.line_item_id AS LineItemId, li.order_id AS OrderId,
                     s.status AS Status, s.photo_urls AS PhotoUrls
              FROM order_line_item_steps s
              JOIN order_line_items li ON li.id = s.line_item_id
              WHERE s.id = @StepId AND s.line_item_id = @LineItemId",
            new { StepId = parsedStepId, LineItemId = itemId });

        if (step is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND",
                $"Step {parsedStepId} not found on line item {itemId}");
        }

        // pending -> started -> complete, forwards only. Re-sending the current status
        // is refused so a double tap cannot silently reset timestamps.
        var legal = (step.Status, targetStatus) switch
        {
            ("pending", "started") => true,
            ("pending", "complete") => true, // a step finished in one go
            ("started", "complete") => true,
            _ => false,
        };

        if (!legal)
        {
            throw new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION",
                $"A step at '{step.Status}' cannot move to '{targetStatus}'");
        }

        var photoPaths = await ValidatePhotosAsync(body.PhotoUrls, step);

        // Photos accumulate across updates — a photo attached when starting a step is
        // not discarded when it completes.
        var existing = string.IsNullOrWhiteSpace(step.PhotoUrls)
            ? []
            : JsonSerializer.Deserialize<List<string>>(step.PhotoUrls) ?? [];
        var allPhotos = existing.Concat(photoPaths).Distinct(StringComparer.Ordinal).ToList();

        var updatedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            @"UPDATE order_line_item_steps
              SET status = @Status,
                  assigned_names = COALESCE(@AssignedNames, assigned_names),
                  photo_urls = @PhotoUrls,
                  started_at = CASE WHEN @Status IN ('started','complete') AND started_at IS NULL
                                    THEN SYSUTCDATETIME() ELSE started_at END,
                  completed_at = CASE WHEN @Status = 'complete' THEN SYSUTCDATETIME() ELSE completed_at END,
                  updated_at = SYSUTCDATETIME()
              OUTPUT inserted.updated_at
              WHERE id = @Id AND status = @ExpectedStatus",
            new
            {
                Id = step.Id,
                Status = targetStatus,
                ExpectedStatus = step.Status,
                AssignedNames = body.AssignedNames is null ? null : JsonSerializer.Serialize(body.AssignedNames),
                PhotoUrls = allPhotos.Count == 0 ? null : JsonSerializer.Serialize(allPhotos),
            });

        if (updatedAt is null)
        {
            throw new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION",
                "Step status changed concurrently — reload and retry");
        }

        // Epic 7 notification hook (item assigned) belongs here.

        var remaining = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM order_line_item_steps WHERE line_item_id = @Id AND status <> 'complete'",
            new { Id = itemId });

        return new OkObjectResult(new
        {
            stepId = step.Id,
            lineItemId = itemId,
            status = targetStatus,
            photos = allPhotos.Select(p => new { blobPath = p, url = photoStorage.CreateReadUrl(p) }).ToList(),
            allStepsComplete = remaining == 0,
            updatedAt = TimeFormat.Utc(updatedAt.Value),
        });
    }

    /// <summary>
    /// The backend never saw these bytes, so everything checkable is checked here:
    /// that the blob belongs to this step, that it actually exists, and that it looks
    /// like an image of a sane size.
    /// </summary>
    private async Task<List<string>> ValidatePhotosAsync(List<string>? submitted, StepRow step)
    {
        if (submitted is null || submitted.Count == 0)
        {
            return [];
        }

        var validated = new List<string>();

        foreach (var path in submitted)
        {
            if (!PhotoPathValidator.BelongsTo(path, step.OrderId, step.LineItemId, step.Id))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    "A submitted photo path does not belong to this production step");
            }

            var info = await photoStorage.GetBlobInfoAsync(path);
            if (info is null)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    "A submitted photo has not been uploaded — upload to the SAS URL before confirming");
            }

            if (info.ContentLength > MaxPhotoBytes)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"Photo exceeds the {MaxPhotoBytes / (1024 * 1024)}MB limit");
            }

            // Content type is set by the uploading client, so this catches mistakes
            // rather than a determined actor — the extension allow-list at SAS-issue
            // time is the stronger control.
            if (info.ContentType is not null && !info.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"Uploaded file is not an image (content type: {info.ContentType})");
            }

            validated.Add(path);
        }

        return validated;
    }
}
