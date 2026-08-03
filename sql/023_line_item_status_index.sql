-- Supports GET /api/order-line-items?status=CARPENTRY — the factory supervisor's
-- dashboard, which lists every item sitting at a given production step across all
-- orders. Without this the query scans order_line_items on every dashboard refresh.
--
-- Composite on (current_status, created_at) because the endpoint filters on status and
-- orders by created_at ASC (oldest-waiting first, so truncation drops the newest rather
-- than burying the item that has been waiting longest).

SET QUOTED_IDENTIFIER ON;
GO

CREATE INDEX ix_order_line_items_status_created
    ON order_line_items(current_status, created_at)
    INCLUDE (order_id, method);
GO
