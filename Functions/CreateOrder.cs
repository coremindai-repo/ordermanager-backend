using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using OrderManager.Backend.Lib;
using OrderManager.Backend.Lib.Orders;
using OrderManager.Backend.Lib.Soho;
using OrderManager.Backend.Lib.Workflow;

namespace OrderManager.Backend.Functions;

/// <summary>
/// POST /api/orders — the order submission sequence in contract §7.
///
/// The SOHO call is deliberately made *before* the database transaction opens: it is
/// a synchronous network call to an external system, and holding SQL locks across it
/// would be a poor trade. That ordering is what makes the compensating cancel
/// necessary if the local write then fails (CLAUDE.md §3).
/// </summary>
public class CreateOrder(
    ISqlConnectionFactory connectionFactory,
    JwtService jwtService,
    ITemplateProvider templateProvider,
    ISohoClient sohoClient,
    OrderReader orderReader,
    ILogger<CreateOrder> logger)
{
    /// <summary>
    /// Either a new item to manufacture (ItemName + optional Method/Materials), or a
    /// claim on an existing inventory item (ClaimLineItemId alone).
    /// </summary>
    public record DimensionsRequest(decimal? Length, decimal? Breadth, decimal? Height, string? Unit);

    public record CreateLineItemRequest(
        string? ItemName,
        string? Description,
        string? Method,
        List<JsonElement>? Materials,
        Guid? ClaimLineItemId,
        string? Finish,
        DimensionsRequest? Dimensions)
    {
        public bool IsClaim => ClaimLineItemId is not null;
    }

    public record CreateOrderRequest(
        string OrderType,
        Guid? StoreId,
        List<CreateLineItemRequest>? LineItems,
        JsonElement? BillTo,
        JsonElement? ShipTo);

    private static readonly string[] ValidMethods = ["factory", "outsource", "import"];

    [Function("CreateOrder")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequest req)
    {
        var caller = AuthHelper.RequireCaller(req, jwtService);
        var request = await ReadAndValidateAsync(req);

        var processTemplate = await templateProvider.GetActiveAsync(TemplateKind.Process);
        var productionTemplate = await templateProvider.GetActiveAsync(TemplateKind.ProductionStep);

        var orderId = Guid.NewGuid();
        var isCustomerOrder = string.Equals(request.OrderType, "customer", StringComparison.OrdinalIgnoreCase);

        // Step 1/2 of contract §7 — obtain the order number.
        string? sohoOrderRef = null;
        string orderNumber;

        if (isCustomerOrder)
        {
            sohoOrderRef = await sohoClient.CreateDraftSalesOrderAsync(new SohoDraftOrderRequest(
                orderId,
                request.StoreId,
                request.LineItems!.Select(li => new SohoLineItem(li.ItemName, li.Description)).ToList()));

            orderNumber = OrderNumberFormatter.ForCustomerOrder(sohoOrderRef);
        }
        else
        {
            using var seqConnection = connectionFactory.CreateConnection();
            var sequence = await seqConnection.ExecuteScalarAsync<long>(
                "SELECT NEXT VALUE FOR seq_stock_order_number");
            orderNumber = OrderNumberFormatter.ForStockOrder(DateTime.UtcNow, sequence);
        }

        try
        {
            await PersistAsync(request, caller, orderId, orderNumber, sohoOrderRef, isCustomerOrder,
                processTemplate.InitialStatus, productionTemplate.InitialStatus);
        }
        catch (Exception ex)
        {
            // Compensate: the draft exists in SOHO but nothing exists locally, so the
            // draft must be voided rather than left orphaned.
            if (sohoOrderRef is not null)
            {
                logger.LogError(ex,
                    "Local write failed after SOHO draft {SohoOrderRef} was created — cancelling the draft",
                    sohoOrderRef);
                try
                {
                    await sohoClient.CancelDraftSalesOrderAsync(sohoOrderRef);
                }
                catch (Exception cancelEx)
                {
                    // Surface loudly: a draft is now stranded in SOHO and needs manual cleanup.
                    logger.LogError(cancelEx,
                        "COMPENSATION FAILED — SOHO draft {SohoOrderRef} is orphaned and needs manual cancellation",
                        sohoOrderRef);
                }
            }

            throw;
        }

        var created = await orderReader.GetDetailAsync(orderId);
        return new ObjectResult(created) { StatusCode = StatusCodes.Status201Created };
    }

    private async Task<CreateOrderRequest> ReadAndValidateAsync(HttpRequest req)
    {
        var body = await req.ReadFromJsonAsync<CreateOrderRequest>();

        if (body is null)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "A request body is required");
        }

        if (!string.Equals(body.OrderType, "customer", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(body.OrderType, "stock", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "orderType must be \"customer\" or \"stock\"");
        }

        if (body.LineItems is null || body.LineItems.Count == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "At least one line item is required");
        }

        foreach (var item in body.LineItems)
        {
            if (item.IsClaim)
            {
                // A claim brings its own name, method and production history with it, so
                // supplying those alongside would be ambiguous about which wins.
                if (!string.IsNullOrWhiteSpace(item.ItemName) || item.Method is not null)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                        "A line item claiming existing inventory must not also supply itemName or method — those come from the claimed item");
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.ItemName))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    "Every line item requires either an itemName or a claimLineItemId");
            }

            if (item.Method is not null && !ValidMethods.Contains(item.Method, StringComparer.OrdinalIgnoreCase))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"method must be one of: {string.Join(", ", ValidMethods)}");
            }
        }

        var duplicateClaims = body.LineItems
            .Where(i => i.IsClaim)
            .GroupBy(i => i.ClaimLineItemId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicateClaims.Count > 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "The same inventory item cannot be claimed twice on one order");
        }

        return body;
    }

    private async Task PersistAsync(
        CreateOrderRequest request,
        Caller caller,
        Guid orderId,
        string orderNumber,
        string? sohoOrderRef,
        bool isCustomerOrder,
        string initialOrderStatus,
        string initialLineItemStatus)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync();

        if (request.StoreId is not null)
        {
            var storeExists = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM stores WHERE id = @Id AND active = 1", new { Id = request.StoreId });
            if (storeExists == 0)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"Store {request.StoreId} does not exist or is inactive");
            }
        }

        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            @"INSERT INTO orders (id, order_number, order_type, soho_order_ref, current_status, store_id, created_by)
              VALUES (@Id, @OrderNumber, @OrderType, @SohoOrderRef, @Status, @StoreId, @CreatedBy)",
            new
            {
                Id = orderId,
                OrderNumber = orderNumber,
                OrderType = isCustomerOrder ? "customer" : "stock",
                SohoOrderRef = sohoOrderRef,
                Status = initialOrderStatus,
                request.StoreId,
                CreatedBy = caller.UserId,
            },
            transaction);

        // The order's starting state belongs in history too, so the audit trail is
        // complete from creation rather than from the first transition.
        await connection.ExecuteAsync(
            @"INSERT INTO order_status_history (order_id, from_status, to_status, user_id, notes)
              VALUES (@OrderId, NULL, @Status, @UserId, 'Order created')",
            new { OrderId = orderId, Status = initialOrderStatus, UserId = caller.UserId },
            transaction);

        foreach (var item in request.LineItems!)
        {
            if (item.IsClaim)
            {
                await ClaimInventoryItemAsync(connection, transaction, item.ClaimLineItemId!.Value, orderId, orderNumber, caller);
                continue;
            }

            var lineItemId = Guid.NewGuid();
            var dimensions = LineItemDimensions.Normalise(
                item.Dimensions is null
                    ? null
                    : new DimensionsInput(
                        item.Dimensions.Length, item.Dimensions.Breadth,
                        item.Dimensions.Height, item.Dimensions.Unit));

            await connection.ExecuteAsync(
                @"INSERT INTO order_line_items
                    (id, order_id, item_name, description, current_status, method, availability_status,
                     finish, dimension_length_cm, dimension_breadth_cm, dimension_height_cm, dimension_unit_entered)
                  VALUES
                    (@Id, @OrderId, @ItemName, @Description, @Status, @Method, @Availability,
                     @Finish, @LengthCm, @BreadthCm, @HeightCm, @DimensionUnit)",
                new
                {
                    Id = lineItemId,
                    OrderId = orderId,
                    item.ItemName,
                    item.Description,
                    Status = initialLineItemStatus,
                    Method = item.Method?.ToLowerInvariant(),
                    item.Finish,
                    // Converted here, once, so the stored value is always centimetres —
                    // see Lib/Orders/LineItemDimensions.cs.
                    LengthCm = dimensions.Length,
                    BreadthCm = dimensions.Breadth,
                    HeightCm = dimensions.Height,
                    DimensionUnit = dimensions.EnteredUnit,
                    // Stock items are inventory from the moment they exist; a customer's
                    // own item never is, so it carries no availability at all.
                    Availability = isCustomerOrder ? null : "available",
                },
                transaction);

            await connection.ExecuteAsync(
                @"INSERT INTO line_item_status_history (line_item_id, from_status, to_status, user_id, notes)
                  VALUES (@LineItemId, NULL, @Status, @UserId, 'Line item created')",
                new { LineItemId = lineItemId, Status = initialLineItemStatus, UserId = caller.UserId },
                transaction);

            foreach (var material in item.Materials ?? [])
            {
                await connection.ExecuteAsync(
                    "INSERT INTO materials (line_item_id, details) VALUES (@LineItemId, @Details)",
                    new { LineItemId = lineItemId, Details = material.GetRawText() },
                    transaction);
            }
        }

        if (request.BillTo is not null || request.ShipTo is not null)
        {
            await connection.ExecuteAsync(
                @"INSERT INTO billing_shipping_details (order_id, bill_to, ship_to)
                  VALUES (@OrderId, @BillTo, @ShipTo)",
                new
                {
                    OrderId = orderId,
                    BillTo = request.BillTo?.GetRawText(),
                    ShipTo = request.ShipTo?.GetRawText(),
                },
                transaction);
        }

        transaction.Commit();
    }

    private record ClaimableItem(
        Guid Id, Guid OrderId, string ItemName, string CurrentStatus, string OrderType, string? AvailabilityStatus);

    /// <summary>
    /// Attaches an existing stock item to the new order.
    ///
    /// `order_id` is reassigned to the claiming order and `originating_order_id` records
    /// where the item was made, so every existing query — gate A, order detail, the
    /// dashboard — keeps meaning "the order responsible for delivering this" and
    /// correctly follows the item.
    ///
    /// The item keeps its id, its production status and all of its
    /// `order_line_item_steps`, so a semi-finished item arrives with its completed work
    /// intact and only needs its remaining steps planned.
    /// </summary>
    private async Task ClaimInventoryItemAsync(
        SqlConnection connection,
        IDbTransaction transaction,
        Guid lineItemId,
        Guid claimingOrderId,
        string claimingOrderNumber,
        Caller caller)
    {
        var item = await connection.QuerySingleOrDefaultAsync<ClaimableItem>(
            @"SELECT li.id AS Id, li.order_id AS OrderId, li.item_name AS ItemName,
                     li.current_status AS CurrentStatus, o.order_type AS OrderType,
                     li.availability_status AS AvailabilityStatus
              FROM order_line_items li
              JOIN orders o ON o.id = li.order_id
              WHERE li.id = @Id",
            new { Id = lineItemId }, transaction);

        if (item is null)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"Line item {lineItemId} does not exist");
        }

        // Checked before the order-type rule: a claimed item's order_id now points at
        // the claiming customer order, so the order-type check would otherwise fire
        // first and report a misleading reason for what is really "already claimed".
        if (item.AvailabilityStatus is "pending_sale" or "sold")
        {
            throw new AppException(StatusCodes.Status409Conflict, "ITEM_NOT_AVAILABLE",
                item.AvailabilityStatus == "sold"
                    ? $"'{item.ItemName}' has already been sold"
                    : $"'{item.ItemName}' is already claimed by another order");
        }

        if (!string.Equals(item.OrderType, "stock", StringComparison.OrdinalIgnoreCase))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                "Only items from stock orders can be claimed — an item on a customer order is already committed to that customer");
        }

        if (item.CurrentStatus is not ("FINISHED" or "SEMI_FINISHED"))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                $"Item is at '{item.CurrentStatus}' and is not claimable — only finished or semi-finished stock is inventory");
        }

        // Optimistic guard on availability, not a read-then-write: two salespeople
        // claiming the same item at once must not both succeed.
        //
        // originating_order_id is set only if not already set, so it always records
        // where the goods were MADE rather than whoever held them last.
        var claimed = await connection.ExecuteAsync(
            @"UPDATE order_line_items
              SET originating_order_id = COALESCE(originating_order_id, order_id),
                  order_id = @ClaimingOrderId,
                  availability_status = 'pending_sale',
                  updated_at = SYSUTCDATETIME()
              WHERE id = @Id AND availability_status = 'available'",
            new { Id = lineItemId, ClaimingOrderId = claimingOrderId }, transaction);

        if (claimed == 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "ITEM_NOT_AVAILABLE",
                $"'{item.ItemName}' is no longer available — it has already been claimed or sold");
        }

        // Recorded in the item's own history so the claim is visible in the audit trail.
        // Production status does not change, so from and to are the same; the note is
        // what carries the meaning.
        await connection.ExecuteAsync(
            @"INSERT INTO line_item_status_history (line_item_id, from_status, to_status, user_id, notes)
              VALUES (@LineItemId, @Status, @Status, @UserId, @Notes)",
            new
            {
                LineItemId = lineItemId,
                Status = item.CurrentStatus,
                UserId = caller.UserId,
                Notes = $"Claimed from stock into order {claimingOrderNumber}",
            },
            transaction);
    }
}
