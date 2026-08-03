using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Notifications;
using OrderManager.Backend.Lib.RawMaterials;

namespace OrderManager.Backend.Functions;

/// <summary>
/// Raw material requests (contract §6). Supplier contact is manual and outside the
/// app — these endpoints only record the resulting status.
/// </summary>
public class RawMaterialRequests(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    INotificationService notifications,
    ILogger<RawMaterialRequests> logger)
{
    public record CreateRequest(JsonElement? Items, JsonElement? Supplier, string? Notes, Guid? LineItemId);

    public record StatusUpdateRequest(string Status, JsonElement? Supplier, string? Notes);

    private record RequestRow(Guid Id, string Status);

    private record LineItemContext(Guid LineItemId, Guid OrderId, Guid OrderCreatedBy);

    // ---------- GET /api/raw-material-requests ----------

    [Function("GetRawMaterialRequests")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw-material-requests")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        var status = req.Query["status"].FirstOrDefault();
        if (status is not null && !RawMaterialStatusFlow.IsKnown(status))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"status must be one of: {string.Join(", ", RawMaterialStatusFlow.Ordered)}");
        }

        Guid? lineItemFilter = null;
        var lineItemIdParam = req.Query["lineItemId"].FirstOrDefault();
        if (lineItemIdParam is not null)
        {
            if (!Guid.TryParse(lineItemIdParam, out var parsed))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    "lineItemId must be a GUID");
            }
            lineItemFilter = parsed;
        }

        // Transcription of AccessScope.CanViewRawMaterialRequest — that method states the
        // rule and carries the tests; this pushes it into the query so the database never
        // hands back a row the caller may not see. Change one, change both.
        //
        // Procurement roles skip the whole clause. Everyone else sees requests they
        // raised, plus item-linked requests on orders they can access — an item-linked
        // request describes the item, so it follows the item rather than its raiser.
        var seesAllProcurement = AccessScope.CanViewAllProcurement(caller.Roles);
        var seesAllOrders = AccessScope.CanViewAllOrders(caller.Roles);

        using var connection = connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync(
            @"SELECT r.id, r.items, r.status, r.supplier, r.notes, r.created_at, r.updated_at,
                     u.id AS requested_by_id, u.first_name, u.last_name,
                     li.id AS line_item_id, li.item_name, li.current_status AS line_item_status,
                     o.id AS order_id, o.order_number
              FROM raw_material_requests r
              JOIN users u ON u.id = r.requested_by
              LEFT JOIN order_line_items li ON li.id = r.line_item_id
              LEFT JOIN orders o ON o.id = li.order_id
              WHERE (@Status IS NULL OR r.status = @Status)
                AND (@LineItemId IS NULL OR r.line_item_id = @LineItemId)
                AND (
                    @SeesAllProcurement = 1
                    OR r.requested_by = @UserId
                    OR (r.line_item_id IS NOT NULL
                        AND (@SeesAllOrders = 1 OR o.created_by = @UserId))
                )
              ORDER BY r.created_at DESC",
            new
            {
                Status = status is null ? null : RawMaterialStatusFlow.Canonical(status),
                LineItemId = lineItemFilter,
                SeesAllProcurement = seesAllProcurement ? 1 : 0,
                SeesAllOrders = seesAllOrders ? 1 : 0,
                UserId = caller.UserId,
            });

        var requests = rows.Select(r => new
        {
            requestId = (Guid)r.id,
            items = ParseJson((string)r.items),
            status = (string)r.status,
            nextStatus = RawMaterialStatusFlow.Next((string)r.status),
            supplier = ParseJson((string?)r.supplier),
            notes = (string?)r.notes,
            // Null for a stock-level request that belongs to no particular item. Present
            // when a supervisor raised it against the item whose production is waiting.
            lineItem = r.line_item_id is null
                ? null
                : new
                {
                    lineItemId = (Guid)r.line_item_id,
                    itemName = (string)r.item_name,
                    currentStatus = (string)r.line_item_status,
                    orderId = (Guid)r.order_id,
                    orderNumber = (string)r.order_number,
                },
            requestedBy = new
            {
                userId = (Guid)r.requested_by_id,
                name = $"{r.first_name} {r.last_name}",
            },
            createdAt = TimeFormat.Utc((DateTime)r.created_at),
            updatedAt = TimeFormat.Utc((DateTime)r.updated_at),
        }).ToList();

        return new OkObjectResult(new { requests, count = requests.Count });
    }

    // ---------- POST /api/raw-material-requests ----------

    [Function("CreateRawMaterialRequest")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "raw-material-requests")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        var body = await req.ReadFromJsonAsync<CreateRequest>();
        if (body?.Items is null || body.Items.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "items is required and must be a JSON array or object");
        }

        if (body.Items.Value.ValueKind == JsonValueKind.Array && body.Items.Value.GetArrayLength() == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "items must not be empty");
        }

        var id = Guid.NewGuid();

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        // lineItemId is optional — a store manager's stock-level request names no item.
        // When it IS given, the caller must be allowed to see that item's order, or a
        // request could be attached to work they have no visibility of (and would then
        // become visible to them through the item-linked branch of the read rule).
        if (body.LineItemId is Guid lineItemId)
        {
            var context = await connection.QuerySingleOrDefaultAsync<LineItemContext>(
                @"SELECT li.id AS LineItemId, o.id AS OrderId, o.created_by AS OrderCreatedBy
                  FROM order_line_items li
                  JOIN orders o ON o.id = li.order_id
                  WHERE li.id = @Id",
                new { Id = lineItemId });

            if (context is null)
            {
                throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND",
                    $"Line item {lineItemId} not found");
            }

            if (!AccessScope.CanAccessOrder(caller.Roles, caller.UserId, context.OrderCreatedBy))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "FORBIDDEN",
                    "You do not have access to that line item");
            }
        }

        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            @"INSERT INTO raw_material_requests (id, requested_by, items, status, supplier, notes, line_item_id)
              VALUES (@Id, @RequestedBy, @Items, @Status, @Supplier, @Notes, @LineItemId)",
            new
            {
                Id = id,
                RequestedBy = caller.UserId,
                Items = body.Items.Value.GetRawText(),
                Status = RawMaterialStatusFlow.Initial,
                Supplier = body.Supplier?.GetRawText(),
                body.Notes,
                body.LineItemId,
            },
            transaction);

        await connection.ExecuteAsync(
            @"INSERT INTO raw_material_request_history (request_id, from_status, to_status, user_id, notes)
              VALUES (@Id, NULL, @Status, @UserId, 'Request raised')",
            new { Id = id, Status = RawMaterialStatusFlow.Initial, UserId = caller.UserId },
            transaction);

        transaction.Commit();

        return new ObjectResult(new
        {
            requestId = id,
            status = RawMaterialStatusFlow.Initial,
            nextStatus = RawMaterialStatusFlow.Next(RawMaterialStatusFlow.Initial),
            lineItemId = body.LineItemId,
        })
        { StatusCode = StatusCodes.Status201Created };
    }

    // ---------- POST /api/raw-material-requests/{id}/status ----------

    [Function("UpdateRawMaterialRequestStatus")]
    public async Task<IActionResult> UpdateStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "raw-material-requests/{requestId}/status")] HttpRequest req,
        string requestId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        // Procurement drives the chain. Note that *raising* a request is deliberately
        // NOT gated: contract §3 says a factory_supervisor raises raw-material requests,
        // they just do not progress them through supplier contact.
        AuthHelper.RequireRole(caller, "store_manager", "company_manager");

        if (!Guid.TryParse(requestId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "requestId must be a GUID");
        }

        var body = await req.ReadFromJsonAsync<StatusUpdateRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Status))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "status is required");
        }

        if (!RawMaterialStatusFlow.IsKnown(body.Status))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"status must be one of: {string.Join(", ", RawMaterialStatusFlow.Ordered)}");
        }

        var targetStatus = RawMaterialStatusFlow.Canonical(body.Status);

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        var request = await connection.QuerySingleOrDefaultAsync<RequestRow>(
            "SELECT id AS Id, status AS Status FROM raw_material_requests WHERE id = @Id",
            new { Id = id });

        if (request is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Raw material request {id} not found");
        }

        if (!RawMaterialStatusFlow.CanTransition(request.Status, targetStatus))
        {
            var next = RawMaterialStatusFlow.Next(request.Status);
            throw new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION",
                next is null
                    ? $"A request at '{request.Status}' is complete and cannot move further"
                    : $"A request at '{request.Status}' can only move to '{next}', not '{targetStatus}'");
        }

        using var transaction = connection.BeginTransaction();

        // WHERE pins the status we validated, so two concurrent updates cannot both win.
        var updatedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            @"UPDATE raw_material_requests
              SET status = @Status,
                  supplier = COALESCE(@Supplier, supplier),
                  updated_at = SYSUTCDATETIME()
              OUTPUT inserted.updated_at
              WHERE id = @Id AND status = @ExpectedStatus",
            new
            {
                Id = id,
                Status = targetStatus,
                ExpectedStatus = request.Status,
                Supplier = body.Supplier?.GetRawText(),
            },
            transaction);

        if (updatedAt is null)
        {
            throw new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION",
                "Request status changed concurrently — reload and retry");
        }

        await connection.ExecuteAsync(
            @"INSERT INTO raw_material_request_history (request_id, from_status, to_status, user_id, notes)
              VALUES (@Id, @From, @To, @UserId, @Notes)",
            new { Id = id, From = request.Status, To = targetStatus, UserId = caller.UserId, body.Notes },
            transaction);

        transaction.Commit();

        // Arrival is the notification-worthy moment (CLAUDE.md §5) — the factory is
        // waiting on these materials. Never allowed to undo the status change.
        if (targetStatus == RawMaterialStatusFlow.Terminal)
        {
            try
            {
                await notifications.NotifyAsync(new NotificationEvent(
                    "raw_material_received",
                    "Raw materials received",
                    "A raw material request has been marked received."));
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Raw material request {RequestId} was marked received but its notification failed", id);
            }
        }

        return new OkObjectResult(new
        {
            requestId = id,
            previousStatus = request.Status,
            status = targetStatus,
            nextStatus = RawMaterialStatusFlow.Next(targetStatus),
            updatedAt = TimeFormat.Utc(updatedAt.Value),
        });
    }

    private static JsonNode? ParseJson(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
}
