using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Photos;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// GET /api/order-line-items (contract §5) — every item currently sitting at a given
/// production step, across all orders. The factory supervisor's dashboard: they work a
/// queue of items, not one order at a time, and until now the only way to reach a line
/// item was through the order that contains it.
///
/// Filters on `status`, which is the line item's own current_status. `currentStep` is a
/// mirror of the same value (see the note in the response shape below) and is not what
/// the filter reads.
/// </summary>
public class ListLineItems(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider,
    IPhotoStorage photoStorage)
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private static readonly string[] ValidMethods = ["factory", "outsource", "import"];

    [Function("ListLineItems")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "order-line-items")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        var template = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);

        // Validated against the template rather than a hardcoded list, so a client whose
        // factory has different stages needs no code change. A typo returns 400 with the
        // real options instead of silently listing nothing, which reads as "no work".
        string? status = null;
        var statusParam = req.Query["status"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(statusParam))
        {
            if (!template.Statuses.Any(s => string.Equals(s.Code, statusParam, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"status must be one of: {string.Join(", ", template.Statuses.Select(s => s.Code))}");
            }
            status = template.ResolveStatusCode(statusParam);
        }

        var method = req.Query["method"].FirstOrDefault();
        if (method is not null && !ValidMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"method must be one of: {string.Join(", ", ValidMethods)}");
        }

        Guid? orderId = null;
        var orderIdParam = req.Query["orderId"].FirstOrDefault();
        if (orderIdParam is not null)
        {
            if (!Guid.TryParse(orderIdParam, out var parsedOrderId))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    "orderId must be a GUID");
            }
            orderId = parsedOrderId;
        }

        var limit = DefaultLimit;
        var limitParam = req.Query["limit"].FirstOrDefault();
        if (limitParam is not null)
        {
            if (!int.TryParse(limitParam, out limit) || limit < 1 || limit > MaxLimit)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"limit must be between 1 and {MaxLimit}");
            }
        }

        // Same rule as GET /api/orders: an item is visible exactly when its order is.
        // A supervisor sees the whole factory queue; a plain salesperson sees only items
        // on orders they raised.
        var requestedMine = string.Equals(req.Query["mine"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
        var restrictToOwn = AccessScope.RestrictOrdersToOwn(caller.Roles, requestedMine);

        using var connection = connectionFactory.CreateConnection();

        // One extra row is fetched so truncation can be reported honestly rather than the
        // caller assuming a full page is the whole queue.
        var rows = (await connection.QueryAsync(
            @"SELECT TOP (@Take)
                     li.id, li.item_name, li.description, li.current_status, li.method,
                     li.current_step, li.availability_status, li.finish,
                     li.reference_photo_urls, li.created_at, li.updated_at,
                     li.dimension_length_cm, li.dimension_breadth_cm,
                     li.dimension_height_cm, li.dimension_unit_entered,
                     li.originating_order_id, oo.order_number AS originating_order_number,
                     o.id AS order_id, o.order_number, o.order_type,
                     o.current_status AS order_status,
                     s.name AS store_name,
                     u.first_name, u.last_name
              FROM order_line_items li
              JOIN orders o ON o.id = li.order_id
              LEFT JOIN orders oo ON oo.id = li.originating_order_id
              LEFT JOIN stores s ON s.id = o.store_id
              JOIN users u ON u.id = o.created_by
              WHERE (@Status IS NULL OR li.current_status = @Status)
                AND (@Method IS NULL OR li.method = @Method)
                AND (@OrderId IS NULL OR li.order_id = @OrderId)
                AND (@RestrictToOwn = 0 OR o.created_by = @UserId)
                AND o.is_test_data = 0
              ORDER BY li.created_at",
            new
            {
                Take = limit + 1,
                Status = status,
                Method = method?.ToLowerInvariant(),
                OrderId = orderId,
                RestrictToOwn = restrictToOwn ? 1 : 0,
                UserId = caller.UserId,
            })).ToList();

        var truncated = rows.Count > limit;
        if (truncated)
        {
            rows = rows.Take(limit).ToList();
        }

        // Steps are fetched for the page that survived truncation, so a large limit does
        // not drag in step rows for items the caller never sees.
        var pageIds = rows.Select(li => (Guid)li.id).ToList();
        List<dynamic> steps = pageIds.Count == 0
            ? []
            : (await connection.QueryAsync(
                @"SELECT st.id, st.line_item_id, st.step_name, st.sequence, st.status,
                         st.assigned_names, st.started_at, st.completed_at
                  FROM order_line_item_steps st
                  WHERE st.line_item_id IN @Ids
                  ORDER BY st.sequence",
                new { Ids = pageIds })).ToList();

        var lineItems = rows.Select(li => new
        {
            lineItemId = (Guid)li.id,
            itemName = (string)li.item_name,
            description = (string?)li.description,
            currentStatus = (string)li.current_status,
            // Mirrors currentStatus exactly; retained for compatibility. Read
            // currentStatus for anything new.
            currentStep = (string?)li.current_step,
            method = (string?)li.method,
            availabilityStatus = (string?)li.availability_status,
            // Non-null only for an item claimed from stock. A claimed semi-finished item
            // shows up in this queue needing its remaining steps, and this is the only
            // place the order that MADE it is visibly connected to the one delivering it
            // — §4 says surface it where provenance matters, and a work queue is that.
            originatingOrderId = (Guid?)li.originating_order_id,
            originatingOrderNumber = (string?)li.originating_order_number,
            finish = (string?)li.finish,
            dimensions = li.dimension_unit_entered is null
                ? null
                : new
                {
                    lengthCm = (decimal?)li.dimension_length_cm,
                    breadthCm = (decimal?)li.dimension_breadth_cm,
                    heightCm = (decimal?)li.dimension_height_cm,
                    enteredUnit = (string)li.dimension_unit_entered,
                },
            // Enough order context to act without a second fetch per row — which order,
            // whose, and where it is going.
            order = new
            {
                orderId = (Guid)li.order_id,
                orderNumber = (string)li.order_number,
                orderType = (string)li.order_type,
                currentStatus = (string)li.order_status,
                storeName = (string?)li.store_name,
                salespersonName = $"{li.first_name} {li.last_name}",
            },
            // Included because the supervisor identifies an item by sight. Step photos
            // are NOT — there can be many per item and they are only wanted on the step
            // screen, which loads the order detail anyway.
            referencePhotos = BuildPhotoLinks((string?)li.reference_photo_urls),
            // Carried inline because acting on a step needs its stepId, and fetching the
            // parent order per row to obtain one would defeat the point of this endpoint.
            productionSteps = steps
                .Where(s => (Guid)s.line_item_id == (Guid)li.id)
                .Select(s => new
                {
                    stepId = (Guid)s.id,
                    stepName = (string)s.step_name,
                    sequence = (int)s.sequence,
                    status = (string)s.status,
                    assignedNames = ParseJson((string?)s.assigned_names),
                    startedAt = s.started_at is null ? null : TimeFormat.Utc((DateTime)s.started_at),
                    completedAt = s.completed_at is null ? null : TimeFormat.Utc((DateTime)s.completed_at),
                })
                .ToList(),
            createdAt = TimeFormat.Utc((DateTime)li.created_at),
            updatedAt = TimeFormat.Utc((DateTime)li.updated_at),
        }).ToList();

        return new OkObjectResult(new
        {
            lineItems,
            count = lineItems.Count,
            limit,
            // True when more items match than were returned. Oldest-waiting items sort
            // first, so what gets dropped is the newest arrivals, not the longest waits.
            truncated,
        });
    }

    private List<object> BuildPhotoLinks(string? photoUrlsJson)
    {
        if (string.IsNullOrWhiteSpace(photoUrlsJson))
        {
            return [];
        }

        var paths = JsonSerializer.Deserialize<List<string>>(photoUrlsJson) ?? [];
        return paths
            .Select(object (p) => new { blobPath = p, url = photoStorage.CreateReadUrl(p) })
            .ToList();
    }

    private static JsonNode? ParseJson(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
}
