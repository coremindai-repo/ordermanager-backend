using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using OrderManager.Backend.Lib.Photos;

namespace OrderManager.Backend.Lib.Orders;

/// <summary>
/// Loads the full order detail shape (contract §4 GET /api/orders/{orderId}).
/// Shared so that the POST /api/orders response and the GET detail response cannot
/// drift apart — the contract says submission returns the created order.
/// </summary>
public sealed class OrderReader(ISqlConnectionFactory connectionFactory, IPhotoStorage photoStorage)
{
    public async Task<object?> GetDetailAsync(Guid orderId)
    {
        using var connection = connectionFactory.CreateConnection();

        using var multi = await connection.QueryMultipleAsync(
            @"SELECT o.id, o.order_number, o.order_type, o.soho_order_ref, o.current_status,
                     o.created_at, o.updated_at,
                     s.id AS store_id, s.name AS store_name, s.location AS store_location,
                     u.id AS user_id, u.first_name, u.last_name
              FROM orders o
              LEFT JOIN stores s ON s.id = o.store_id
              JOIN users u ON u.id = o.created_by
              WHERE o.id = @Id;

              SELECT bill_to, ship_to FROM billing_shipping_details WHERE order_id = @Id;

              SELECT id, item_name, description, current_status, method, current_step
              FROM order_line_items WHERE order_id = @Id ORDER BY created_at;

              SELECT m.line_item_id, m.details
              FROM materials m
              JOIN order_line_items li ON li.id = m.line_item_id
              WHERE li.order_id = @Id;

              SELECT s.id, s.line_item_id, s.step_name, s.sequence, s.status,
                     s.assigned_names, s.photo_urls, s.started_at, s.completed_at
              FROM order_line_item_steps s
              JOIN order_line_items li ON li.id = s.line_item_id
              WHERE li.order_id = @Id
              ORDER BY s.sequence;",
            new { Id = orderId });

        var order = await multi.ReadSingleOrDefaultAsync();
        if (order is null)
        {
            return null;
        }

        var addresses = await multi.ReadSingleOrDefaultAsync();
        var lineItems = (await multi.ReadAsync()).ToList();
        var materials = (await multi.ReadAsync()).ToList();
        var steps = (await multi.ReadAsync()).ToList();

        return new
        {
            orderId = (Guid)order.id,
            orderNumber = (string)order.order_number,
            orderType = (string)order.order_type,
            sohoOrderRef = (string?)order.soho_order_ref,
            currentStatus = (string)order.current_status,
            createdAt = TimeFormat.Utc((DateTime)order.created_at),
            updatedAt = TimeFormat.Utc((DateTime)order.updated_at),
            salesperson = new
            {
                userId = (Guid)order.user_id,
                firstName = (string)order.first_name,
                lastName = (string)order.last_name,
            },
            store = order.store_id is null
                ? null
                : new
                {
                    storeId = (Guid)order.store_id,
                    name = (string)order.store_name,
                    location = (string?)order.store_location,
                },
            billTo = ParseJson(addresses?.bill_to as string),
            shipTo = ParseJson(addresses?.ship_to as string),
            lineItems = lineItems.Select(li => new
            {
                lineItemId = (Guid)li.id,
                itemName = (string)li.item_name,
                description = (string?)li.description,
                currentStatus = (string)li.current_status,
                method = (string?)li.method,
                currentStep = (string?)li.current_step,
                materials = materials
                    .Where(m => (Guid)m.line_item_id == (Guid)li.id)
                    .Select(m => ParseJson((string)m.details))
                    .ToList(),
                productionSteps = steps
                    .Where(s => (Guid)s.line_item_id == (Guid)li.id)
                    .Select(s => new
                    {
                        stepId = (Guid)s.id,
                        stepName = (string)s.step_name,
                        sequence = (int)s.sequence,
                        status = (string)s.status,
                        assignedNames = ParseJson((string?)s.assigned_names),
                        photos = BuildPhotoLinks((string?)s.photo_urls),
                        startedAt = s.started_at is null ? null : TimeFormat.Utc((DateTime)s.started_at),
                        completedAt = s.completed_at is null ? null : TimeFormat.Utc((DateTime)s.completed_at),
                    })
                    .ToList(),
            }).ToList(),
        };
    }

    /// <summary>
    /// Only blob paths are stored. Read URLs are minted per response and expire
    /// quickly, so nothing long-lived leaks into logs or client caches.
    /// </summary>
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

    /// <summary>
    /// Emits stored JSON columns as real JSON in the response rather than as an
    /// escaped string.
    /// </summary>
    private static JsonNode? ParseJson(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
}
