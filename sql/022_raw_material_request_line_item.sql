-- Raw material requests may name the line item they were raised for.
--
-- The supervisor's per-item screen needs to show "materials on order" against the item
-- whose production is waiting on them. Until now a request stood alone, so an item-level
-- need could only be recorded in the free-form items JSON, where nothing could query it.
--
-- The column is NULLABLE and stays that way. A store manager still raises stock-level
-- requests that belong to no item, and those must keep working exactly as before. Same
-- optional-provenance pattern as order_line_items.originating_order_id.
--
-- DELIBERATELY NOT A GATE. An unreceived request does not block a production step. The
-- supervisor moves steps by hand and already decides whether materials are on hand;
-- a system gate would only duplicate a judgment that is theirs to make. If that ever
-- changes it belongs in the production step template as a transition flag, not here.
--
-- ON DELETE is left as NO ACTION (the default). A line item with an outstanding raw
-- material request should not be silently deletable, and nothing in this scope deletes
-- line items anyway.

SET QUOTED_IDENTIFIER ON;
GO

ALTER TABLE raw_material_requests
    ADD line_item_id UNIQUEIDENTIFIER NULL
        REFERENCES order_line_items(id);
GO

-- Supports GET /api/raw-material-requests?lineItemId=... — the supervisor's item screen
-- hits this on every open. Filtered, because the majority of rows are standalone stock
-- requests that this lookup never wants.
CREATE INDEX ix_raw_material_requests_line_item
    ON raw_material_requests(line_item_id)
    WHERE line_item_id IS NOT NULL;
GO
