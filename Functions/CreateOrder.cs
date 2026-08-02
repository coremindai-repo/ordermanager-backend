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
    public record CreateLineItemRequest(string ItemName, string? Description, string? Method, List<JsonElement>? Materials);

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
            if (string.IsNullOrWhiteSpace(item.ItemName))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    "Every line item requires an itemName");
            }

            if (item.Method is not null && !ValidMethods.Contains(item.Method, StringComparer.OrdinalIgnoreCase))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "VALIDATION_ERROR",
                    $"method must be one of: {string.Join(", ", ValidMethods)}");
            }
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
            var lineItemId = Guid.NewGuid();

            await connection.ExecuteAsync(
                @"INSERT INTO order_line_items (id, order_id, item_name, description, current_status, method)
                  VALUES (@Id, @OrderId, @ItemName, @Description, @Status, @Method)",
                new
                {
                    Id = lineItemId,
                    OrderId = orderId,
                    item.ItemName,
                    item.Description,
                    Status = initialLineItemStatus,
                    Method = item.Method?.ToLowerInvariant(),
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
}
