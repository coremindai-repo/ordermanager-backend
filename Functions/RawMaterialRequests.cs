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
    public record CreateRequest(JsonElement? Items, JsonElement? Supplier, string? Notes);

    public record StatusUpdateRequest(string Status, JsonElement? Supplier, string? Notes);

    private record RequestRow(Guid Id, string Status);

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

        // A factory supervisor sees the requests they raised (contract §3); store
        // managers and company managers procure, so they see everything.
        var procurementRoles = new[] { "store_manager", "company_manager" };
        var restrictToOwn = !caller.Roles.Any(r => procurementRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        using var connection = connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync(
            @"SELECT r.id, r.items, r.status, r.supplier, r.notes, r.created_at, r.updated_at,
                     u.id AS requested_by_id, u.first_name, u.last_name
              FROM raw_material_requests r
              JOIN users u ON u.id = r.requested_by
              WHERE (@Status IS NULL OR r.status = @Status)
                AND (@RestrictToOwn = 0 OR r.requested_by = @UserId)
              ORDER BY r.created_at DESC",
            new
            {
                Status = status is null ? null : RawMaterialStatusFlow.Canonical(status),
                RestrictToOwn = restrictToOwn ? 1 : 0,
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
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            @"INSERT INTO raw_material_requests (id, requested_by, items, status, supplier, notes)
              VALUES (@Id, @RequestedBy, @Items, @Status, @Supplier, @Notes)",
            new
            {
                Id = id,
                RequestedBy = caller.UserId,
                Items = body.Items.Value.GetRawText(),
                Status = RawMaterialStatusFlow.Initial,
                Supplier = body.Supplier?.GetRawText(),
                body.Notes,
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
