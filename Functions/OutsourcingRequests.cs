using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Outsourcing;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// Outsourcing / import requests (contract §6). Supplier contact is manual and outside
/// the app; these endpoints record the resulting status.
///
/// Receiving a request also moves its linked line items forward — see
/// <see cref="AdvanceLineItemsAsync"/> for why that coupling exists and how it stays
/// honest.
/// </summary>
public class OutsourcingRequests(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider,
    TransitionValidator validator,
    ILogger<OutsourcingRequests> logger)
{
    public record CreateRequest(string Method, Guid? SupplierId, List<Guid>? LineItemIds, JsonElement? Items, string? Notes);

    public record StatusUpdateRequest(string Status, Guid? SupplierId, string? Notes);

    private record RequestRow(Guid Id, string Status, string Method);

    private record LineItemRow(Guid Id, string CurrentStatus, string? Method);

    /// <summary>Where a line item lands when its request is received. Template status codes.</summary>
    private const string FinishedStatus = "FINISHED";
    private const string SemiFinishedStatus = "SEMI_FINISHED";
    private const string WithSupplierStatus = "WITH_SUPPLIER";

    // ---------- GET /api/outsourcing-requests ----------

    [Function("GetOutsourcingRequests")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "outsourcing-requests")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        // Contract §3: outsourcing/import is company_manager territory — no per-record
        // nuance like raw materials (item-linked vs. standalone), so a flat role gate is
        // the whole rule, matching Create/UpdateStatus below rather than a dedicated
        // AccessScope method for a distinction the contract doesn't draw.
        AuthHelper.RequireRole(caller, "company_manager");

        var status = req.Query["status"].FirstOrDefault();
        if (status is not null && !OutsourcingStatusFlow.IsKnown(status))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"status must be one of: {string.Join(", ", OutsourcingStatusFlow.All)}");
        }

        var method = req.Query["method"].FirstOrDefault();
        if (method is not null && method is not ("outsource" or "import"))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "method must be \"outsource\" or \"import\"");
        }

        using var connection = connectionFactory.CreateConnection();

        var rows = await connection.QueryAsync(
            @"SELECT r.id, r.method, r.status, r.items, r.notes, r.created_at, r.updated_at,
                     s.id AS supplier_id, s.name AS supplier_name,
                     u.id AS requested_by_id, u.first_name, u.last_name,
                     (SELECT COUNT(1) FROM outsourcing_request_line_items l WHERE l.request_id = r.id) AS line_item_count
              FROM outsourcing_requests r
              LEFT JOIN suppliers s ON s.id = r.supplier_id
              JOIN users u ON u.id = r.requested_by
              WHERE (@Status IS NULL OR r.status = @Status)
                AND (@Method IS NULL OR r.method = @Method)
              ORDER BY r.created_at DESC",
            new { Status = status is null ? null : OutsourcingStatusFlow.Canonical(status), Method = method });

        var requests = rows.Select(r => new
        {
            requestId = (Guid)r.id,
            method = (string)r.method,
            status = (string)r.status,
            // Method-aware, so an import request never advertises a semi-finished
            // option it cannot take.
            nextStatuses = OutsourcingStatusFlow.NextOptions((string)r.status, (string)r.method),
            supplier = r.supplier_id is null
                ? null
                : new { supplierId = (Guid)r.supplier_id, name = (string)r.supplier_name },
            items = ParseJson((string?)r.items),
            lineItemCount = (int)r.line_item_count,
            notes = (string?)r.notes,
            requestedBy = new { userId = (Guid)r.requested_by_id, name = $"{r.first_name} {r.last_name}" },
            createdAt = TimeFormat.Utc((DateTime)r.created_at),
            updatedAt = TimeFormat.Utc((DateTime)r.updated_at),
        }).ToList();

        return new OkObjectResult(new { requests, count = requests.Count });
    }

    // ---------- POST /api/outsourcing-requests ----------

    [Function("CreateOutsourcingRequest")]
    public async Task<IActionResult> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "outsourcing-requests")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);

        // Outsourcing/import is company_manager territory (contract §3 gives them the
        // outsourcing/import screens).
        AuthHelper.RequireRole(caller, "company_manager");

        var body = await req.ReadFromJsonAsync<CreateRequest>();
        if (body is null || body.Method is not ("outsource" or "import"))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "method must be \"outsource\" or \"import\"");
        }

        if (body.LineItemIds is null || body.LineItemIds.Count == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "At least one lineItemId is required");
        }

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        if (body.SupplierId is not null)
        {
            var supplierOk = await connection.ExecuteScalarAsync<int>(
                @"SELECT COUNT(1) FROM suppliers
                  WHERE id = @Id AND active = 1
                    AND ((@Method = 'outsource' AND supports_outsource = 1)
                      OR (@Method = 'import' AND supports_import = 1))",
                new { Id = body.SupplierId, body.Method });

            if (supplierOk == 0)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"Supplier {body.SupplierId} does not exist, is inactive, or does not serve '{body.Method}'");
            }
        }

        // Every named line item must exist and already be set to this method — the
        // production plan is where an item's route is chosen, so a request cannot
        // quietly reroute an item that was planned for the factory.
        var items = (await connection.QueryAsync<LineItemRow>(
            @"SELECT id AS Id, current_status AS CurrentStatus, method AS Method
              FROM order_line_items WHERE id IN @Ids",
            new { Ids = body.LineItemIds })).ToList();

        if (items.Count != body.LineItemIds.Distinct().Count())
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "One or more lineItemIds do not exist");
        }

        var mismatched = items.Where(i => !string.Equals(i.Method, body.Method, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mismatched.Count > 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"{mismatched.Count} line item(s) are not set to method '{body.Method}' — set their production plan first");
        }

        var id = Guid.NewGuid();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            @"INSERT INTO outsourcing_requests (id, requested_by, method, supplier_id, items, status, notes)
              VALUES (@Id, @RequestedBy, @Method, @SupplierId, @Items, @Status, @Notes)",
            new
            {
                Id = id,
                RequestedBy = caller.UserId,
                body.Method,
                body.SupplierId,
                Items = body.Items?.GetRawText(),
                Status = OutsourcingStatusFlow.Initial,
                body.Notes,
            },
            transaction);

        foreach (var lineItemId in body.LineItemIds.Distinct())
        {
            await connection.ExecuteAsync(
                "INSERT INTO outsourcing_request_line_items (request_id, line_item_id) VALUES (@RequestId, @LineItemId)",
                new { RequestId = id, LineItemId = lineItemId }, transaction);
        }

        await connection.ExecuteAsync(
            @"INSERT INTO outsourcing_request_history (request_id, from_status, to_status, user_id, notes)
              VALUES (@Id, NULL, @Status, @UserId, 'Request placed')",
            new { Id = id, Status = OutsourcingStatusFlow.Initial, UserId = caller.UserId },
            transaction);

        transaction.Commit();

        // Placing the request is what sends the goods out, so the items move to
        // WITH_SUPPLIER now rather than on acceptance.
        var moved = await AdvanceLineItemsAsync(connection, id, WithSupplierStatus, caller, "Sent to supplier");

        return new ObjectResult(new
        {
            requestId = id,
            method = body.Method,
            status = OutsourcingStatusFlow.Initial,
            nextStatuses = OutsourcingStatusFlow.NextOptions(OutsourcingStatusFlow.Initial, body.Method),
            lineItemsAdvanced = moved,
        })
        { StatusCode = StatusCodes.Status201Created };
    }

    // ---------- POST /api/outsourcing-requests/{id}/status ----------

    [Function("UpdateOutsourcingRequestStatus")]
    public async Task<IActionResult> UpdateStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "outsourcing-requests/{requestId}/status")] HttpRequest req,
        string requestId)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);
        AuthHelper.RequireRole(caller, "company_manager");

        if (!Guid.TryParse(requestId, out var id))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "requestId must be a GUID");
        }

        var body = await req.ReadFromJsonAsync<StatusUpdateRequest>();
        if (body is null || string.IsNullOrWhiteSpace(body.Status) || !OutsourcingStatusFlow.IsKnown(body.Status))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"status must be one of: {string.Join(", ", OutsourcingStatusFlow.All)}");
        }

        var targetStatus = OutsourcingStatusFlow.Canonical(body.Status);

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        var request = await connection.QuerySingleOrDefaultAsync<RequestRow>(
            "SELECT id AS Id, status AS Status, method AS Method FROM outsourcing_requests WHERE id = @Id",
            new { Id = id });

        if (request is null)
        {
            throw new AppException(StatusCodes.Status404NotFound, "NOT_FOUND", $"Outsourcing request {id} not found");
        }

        // Method-aware: only outsourcing can return semi-finished goods.
        if (!OutsourcingStatusFlow.CanTransition(request.Status, targetStatus, request.Method))
        {
            var options = OutsourcingStatusFlow.NextOptions(request.Status, request.Method);

            // Say why, rather than just listing what is allowed — "import cannot be
            // semi-finished" is the actual answer the caller needs.
            if (targetStatus == OutsourcingStatusFlow.ReceivedSemiFinished &&
                !OutsourcingStatusFlow.CanReturnSemiFinished(request.Method))
            {
                throw new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION",
                    $"An '{request.Method}' request cannot be received semi-finished — only outsourcing returns partially completed goods. Use '{OutsourcingStatusFlow.ReceivedFinished}'.");
            }

            throw new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION",
                options.Count == 0
                    ? $"A request at '{request.Status}' is complete and cannot move further"
                    : $"A request at '{request.Status}' can only move to {string.Join(" or ", options.Select(o => $"'{o}'"))}, not '{targetStatus}'");
        }

        using var transaction = connection.BeginTransaction();

        var updatedAt = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            @"UPDATE outsourcing_requests
              SET status = @Status,
                  supplier_id = COALESCE(@SupplierId, supplier_id),
                  updated_at = SYSUTCDATETIME()
              OUTPUT inserted.updated_at
              WHERE id = @Id AND status = @ExpectedStatus",
            new { Id = id, Status = targetStatus, ExpectedStatus = request.Status, body.SupplierId },
            transaction);

        if (updatedAt is null)
        {
            throw new AppException(StatusCodes.Status409Conflict, "ILLEGAL_TRANSITION",
                "Request status changed concurrently — reload and retry");
        }

        await connection.ExecuteAsync(
            @"INSERT INTO outsourcing_request_history (request_id, from_status, to_status, user_id, notes)
              VALUES (@Id, @From, @To, @UserId, @Notes)",
            new { Id = id, From = request.Status, To = targetStatus, UserId = caller.UserId, body.Notes },
            transaction);

        transaction.Commit();

        // The branch that matters: finished goods are done; semi-finished goods go back
        // into the factory step checklist.
        var lineItemsAdvanced = targetStatus switch
        {
            OutsourcingStatusFlow.ReceivedFinished =>
                await AdvanceLineItemsAsync(connection, id, FinishedStatus, caller, "Received finished from supplier"),
            OutsourcingStatusFlow.ReceivedSemiFinished =>
                await AdvanceLineItemsAsync(connection, id, SemiFinishedStatus, caller, "Received semi-finished from supplier"),
            _ => 0,
        };

        return new OkObjectResult(new
        {
            requestId = id,
            previousStatus = request.Status,
            status = targetStatus,
            nextStatuses = OutsourcingStatusFlow.NextOptions(targetStatus, request.Method),
            lineItemsAdvanced,
            // Semi-finished items now need a production plan, same as a factory item.
            requiresProductionPlan = targetStatus == OutsourcingStatusFlow.ReceivedSemiFinished,
            updatedAt = TimeFormat.Utc(updatedAt.Value),
        });
    }

    /// <summary>
    /// Moves the request's line items to <paramref name="targetStatus"/>.
    ///
    /// The coupling is deliberate: the supervisor should not have to record the same
    /// physical event twice, once on the request and once per item. But it runs through
    /// the SAME validator and writes the SAME history rows as a manual line-item
    /// transition, so no template rule is bypassed — an item whose method or status
    /// makes the move illegal is skipped and logged rather than forced.
    /// </summary>
    private async Task<int> AdvanceLineItemsAsync(
        SqlConnection connection, Guid requestId, string targetStatus, Caller caller, string note)
    {
        var template = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);

        var items = (await connection.QueryAsync<LineItemRow>(
            @"SELECT li.id AS Id, li.current_status AS CurrentStatus, li.method AS Method
              FROM order_line_items li
              JOIN outsourcing_request_line_items l ON l.line_item_id = li.id
              WHERE l.request_id = @RequestId",
            new { RequestId = requestId })).ToList();

        var advanced = 0;

        foreach (var item in items)
        {
            var decision = validator.Validate(
                template, item.CurrentStatus, targetStatus, caller.Roles, method: item.Method);

            if (!decision.IsAllowed)
            {
                logger.LogWarning(
                    "Line item {LineItemId} not advanced to {Target} for request {RequestId}: {Reason}",
                    item.Id, targetStatus, requestId, decision.Message);
                continue;
            }

            var canonical = template.ResolveStatusCode(targetStatus);

            using var transaction = connection.BeginTransaction();

            var updated = await connection.ExecuteAsync(
                @"UPDATE order_line_items
                  SET current_status = @To, current_step = @To, updated_at = SYSUTCDATETIME()
                  WHERE id = @Id AND current_status = @From",
                new { Id = item.Id, To = canonical, From = item.CurrentStatus },
                transaction);

            if (updated == 0)
            {
                transaction.Rollback();
                logger.LogWarning(
                    "Line item {LineItemId} changed concurrently and was not advanced to {Target}", item.Id, targetStatus);
                continue;
            }

            await connection.ExecuteAsync(
                @"INSERT INTO line_item_status_history (line_item_id, from_status, to_status, user_id, notes)
                  VALUES (@LineItemId, @From, @To, @UserId, @Notes)",
                new { LineItemId = item.Id, From = item.CurrentStatus, To = canonical, UserId = caller.UserId, Notes = note },
                transaction);

            transaction.Commit();
            advanced++;
        }

        return advanced;
    }

    private static JsonNode? ParseJson(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
}
