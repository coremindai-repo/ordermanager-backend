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
/// Reference photos are captured by the salesperson at item entry — a sourced image the
/// factory supervisor needs when choosing factory/outsource/import and deciding what to
/// build. They are a production input, not a note.
///
/// Scoped to the LINE ITEM, not a production step, because no step exists yet: mobile
/// creates the order first, then immediately requests a SAS per returned lineItemId.
///
/// Same two-call shape as step photos — SAS, then confirm. The confirm call is where
/// ownership, existence, size and content type are checked, because the backend never
/// sees the bytes.
/// </summary>
public class ReferencePhotos(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    IPhotoStorage photoStorage)
{
    public record UploadUrlRequest(string? FileExtension);

    public record ConfirmRequest(List<string>? PhotoUrls);

    private record LineItemRow(Guid Id, Guid OrderId, Guid OrderCreatedBy, string? ReferencePhotoUrls);

    /// <summary>Generous for a phone photo, low enough to refuse a video or mis-picked file.</summary>
    private const long MaxPhotoBytes = 15 * 1024 * 1024;

    // ---------- SAS ----------

    [Function("GetReferencePhotoUploadUrl")]
    public async Task<IActionResult> GetUploadUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "order-line-items/{lineItemId}/reference-photo-upload-url")] HttpRequest req,
        string lineItemId)
    {
        var (caller, item) = await ResolveAsync(req, lineItemId);

        var body = await req.ReadFromJsonAsync<UploadUrlRequest>();
        var extension = string.IsNullOrWhiteSpace(body?.FileExtension) ? ".jpg" : body.FileExtension;

        var target = photoStorage.CreateReferenceUploadTarget(item.OrderId, item.Id, extension);

        return new OkObjectResult(new
        {
            uploadUrl = target.UploadUrl,
            blobPath = target.BlobPath,
            expiresAt = target.ExpiresAt.UtcDateTime.ToString("o"),
            requiredHeaders = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" },
        });
    }

    // ---------- Confirm ----------

    [Function("ConfirmReferencePhotos")]
    public async Task<IActionResult> Confirm(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "order-line-items/{lineItemId}/reference-photos")] HttpRequest req,
        string lineItemId)
    {
        var (caller, item) = await ResolveAsync(req, lineItemId);

        var body = await req.ReadFromJsonAsync<ConfirmRequest>();
        if (body?.PhotoUrls is null || body.PhotoUrls.Count == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "photoUrls is required and must contain at least one blob path");
        }

        foreach (var path in body.PhotoUrls)
        {
            if (!PhotoPathValidator.BelongsToLineItemReference(path, item.OrderId, item.Id))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    "A submitted photo path does not belong to this line item");
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

            if (info.ContentType is not null &&
                !info.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"Uploaded file is not an image (content type: {info.ContentType})");
            }
        }

        // Accumulate, so confirming a second photo does not discard the first.
        var existing = string.IsNullOrWhiteSpace(item.ReferencePhotoUrls)
            ? []
            : JsonSerializer.Deserialize<List<string>>(item.ReferencePhotoUrls) ?? [];

        var all = existing.Concat(body.PhotoUrls).Distinct(StringComparer.Ordinal).ToList();

        using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            @"UPDATE order_line_items
              SET reference_photo_urls = @Paths, updated_at = SYSUTCDATETIME()
              WHERE id = @Id",
            new { Id = item.Id, Paths = JsonSerializer.Serialize(all) });

        return new OkObjectResult(new
        {
            lineItemId = item.Id,
            referencePhotos = all.Select(p => new { blobPath = p, url = photoStorage.CreateReadUrl(p) }).ToList(),
        });
    }

    /// <summary>
    /// Authenticates, loads the line item, and applies the same order-visibility rule the
    /// list endpoints use — a salesperson may attach reference photos to their own
    /// orders, anyone who can see all orders may attach to any.
    /// </summary>
    private async Task<(Caller Caller, LineItemRow Item)> ResolveAsync(HttpRequest req, string lineItemId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        if (!Guid.TryParse(lineItemId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "lineItemId must be a GUID");
        }

        using var connection = connectionFactory.CreateConnection();

        var item = await connection.QuerySingleOrDefaultAsync<LineItemRow>(
            @"SELECT li.id AS Id, li.order_id AS OrderId, o.created_by AS OrderCreatedBy,
                     li.reference_photo_urls AS ReferencePhotoUrls
              FROM order_line_items li
              JOIN orders o ON o.id = li.order_id
              WHERE li.id = @Id",
            new { Id = id });

        if (item is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Line item {id} not found");
        }

        if (!AccessScope.CanAccessOrder(caller.Roles, caller.UserId, item.OrderCreatedBy))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "FORBIDDEN",
                "You may only attach reference photos to orders you raised");
        }

        return (caller, item);
    }
}
