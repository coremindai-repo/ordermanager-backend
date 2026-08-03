-- Inventory claiming — stock items can be claimed into a new order.
--
-- Two new columns on order_line_items.
--
-- ORDER_ID IS REASSIGNED to the claiming order when an item is claimed, and
-- originating_order_id records where it was actually made. That keeps every existing
-- query correct without change: gate A, order detail, the dashboard and the inventory
-- view all mean "the order responsible for delivering this", and a claimed item
-- correctly follows.
--
-- ⚠ REPORTING CONSEQUENCE, worth knowing before writing any query: after a claim,
-- `order_id` answers "who delivers this", NOT "who made this". Production attribution
-- lives in `originating_order_id`, which is NULL until an item is claimed — so the
-- provenance of any item is:
--
--     COALESCE(originating_order_id, order_id)
--
-- Use that, not order_id, for anything reporting on what an order produced.
--
-- originating_order_id is set only on the FIRST claim, so it always names where the
-- goods were made rather than whoever held them last.

SET QUOTED_IDENTIFIER ON;
GO

ALTER TABLE order_line_items
    ADD originating_order_id UNIQUEIDENTIFIER NULL REFERENCES orders(id),
        -- Sale lifecycle, independent of production status. NULL for items that were
        -- manufactured to order (a customer's own item is never "available stock").
        -- Set only on stock-order line items.
        --   available    — made for stock, not yet spoken for
        --   pending_sale — claimed into an order that has not completed
        --   sold         — the claiming order reached a terminal status
        availability_status NVARCHAR(20) NULL
            CHECK (availability_status IN ('available', 'pending_sale', 'sold'));
GO

CREATE INDEX IX_order_line_items_availability
    ON order_line_items (availability_status) WHERE availability_status IS NOT NULL;

CREATE INDEX IX_order_line_items_originating_order
    ON order_line_items (originating_order_id) WHERE originating_order_id IS NOT NULL;
GO

-- Backfill: existing stock-order line items become available stock. Customer-order
-- items stay NULL — they were made for a named customer and were never inventory.
UPDATE li
SET li.availability_status = 'available'
FROM order_line_items li
JOIN orders o ON o.id = li.order_id
WHERE o.order_type = 'stock'
  AND li.availability_status IS NULL;
