-- Epic 4 — process template v4: branch the flow on order type.
--
-- v3 placed READY_TO_INVOICE / READY_TO_DELIVER after the goods reached the store,
-- as an optional route, because the two tracks were believed to be independent.
-- They are not: invoicing happens immediately once production completes, BEFORE any
-- dispatch decision, and it applies to customer orders only. v4 corrects both.
--
--   Customer orders:
--     IN_PRODUCTION --[all items complete]--> READY_TO_INVOICE -> READY_TO_DELIVER
--       -> KEEP_IN_FACTORY | SENT_TO_WAREHOUSE -> ... -> DELIVERED
--
--   Stock orders:
--     IN_PRODUCTION --[all items complete]--> KEEP_IN_FACTORY | SENT_TO_WAREHOUSE
--       -> ... -> DELIVERED          (never touches the two invoice statuses)
--
--   Shared logistics tail (both types):
--     KEEP_IN_FACTORY -> SENT_TO_WAREHOUSE
--     KEEP_IN_FACTORY | SENT_TO_WAREHOUSE --[destination store set]--> IN_TRANSIT
--     IN_TRANSIT -> SENT_TO_STORE -> RECEIVED_IN_STORE -> OUT_FOR_DELIVERY -> DELIVERED
--
-- The branch is expressed with `orderTypes` on the edges leaving IN_PRODUCTION and
-- READY_TO_DELIVER — the order-level counterpart of the `methods` restriction line
-- items already use. Omitting it means the edge applies to both types, which is why
-- the shared tail carries no restriction.
--
-- Rework note: reverting to IN_PRODUCTION from the logistics chain means a customer
-- order re-traverses invoicing on its way back out. That is intended — reworked goods
-- are re-invoiced.
--
-- MIGRATION: v4 removes no statuses, so no order can be stranded (CLAUDE.md §5). The
-- guard below is kept anyway, since v3 orders sitting in READY_TO_INVOICE or
-- READY_TO_DELIVER reached them by the old route; under v4 those are customer-only,
-- so a stock order parked there would no longer be able to move.
--
-- REMINDER: templates are cached per worker instance — redeploy for this to take
-- effect (CLAUDE.md §5).

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

IF EXISTS (
    SELECT 1 FROM orders
    WHERE order_type = 'stock' AND current_status IN ('READY_TO_INVOICE', 'READY_TO_DELIVER')
)
BEGIN
    THROW 50002, 'Stock orders sit in an invoice status — under v4 those are customer-only and such orders would be stranded. Migrate them first.', 1;
END

BEGIN TRANSACTION;

UPDATE process_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO process_templates (client_id, version, active, template_json)
VALUES (@clientId, 4, 1, N'{
  "initialStatus": "NEW",
  "statuses": [
    { "code": "NEW", "name": "New Order Capture" },
    { "code": "IN_PRODUCTION", "name": "In Production" },
    { "code": "READY_TO_INVOICE", "name": "Ready to Invoice" },
    { "code": "READY_TO_DELIVER", "name": "Ready to Deliver" },
    { "code": "KEEP_IN_FACTORY", "name": "Keep in Factory" },
    { "code": "SENT_TO_WAREHOUSE", "name": "Sent to Warehouse" },
    { "code": "IN_TRANSIT", "name": "In Transit" },
    { "code": "SENT_TO_STORE", "name": "Sent to Store" },
    { "code": "RECEIVED_IN_STORE", "name": "Received in Store" },
    { "code": "OUT_FOR_DELIVERY", "name": "Out for Delivery" },
    { "code": "DELIVERED", "name": "Delivered" }
  ],
  "transitions": [
    { "from": "NEW", "to": "IN_PRODUCTION" },

    { "from": "IN_PRODUCTION", "to": "READY_TO_INVOICE",
      "orderTypes": ["customer"], "requiresAllLineItemsComplete": true },
    { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER", "orderTypes": ["customer"] },
    { "from": "READY_TO_DELIVER", "to": "KEEP_IN_FACTORY", "orderTypes": ["customer"] },
    { "from": "READY_TO_DELIVER", "to": "SENT_TO_WAREHOUSE", "orderTypes": ["customer"] },

    { "from": "IN_PRODUCTION", "to": "KEEP_IN_FACTORY",
      "orderTypes": ["stock"], "requiresAllLineItemsComplete": true },
    { "from": "IN_PRODUCTION", "to": "SENT_TO_WAREHOUSE",
      "orderTypes": ["stock"], "requiresAllLineItemsComplete": true },

    { "from": "KEEP_IN_FACTORY", "to": "SENT_TO_WAREHOUSE" },
    { "from": "KEEP_IN_FACTORY", "to": "IN_TRANSIT", "requiresDestinationStore": true },
    { "from": "SENT_TO_WAREHOUSE", "to": "IN_TRANSIT", "requiresDestinationStore": true },
    { "from": "IN_TRANSIT", "to": "SENT_TO_STORE" },
    { "from": "SENT_TO_STORE", "to": "RECEIVED_IN_STORE" },
    { "from": "RECEIVED_IN_STORE", "to": "OUT_FOR_DELIVERY" },
    { "from": "OUT_FOR_DELIVERY", "to": "DELIVERED" },

    { "from": "KEEP_IN_FACTORY", "to": "IN_PRODUCTION", "revert": true },
    { "from": "SENT_TO_WAREHOUSE", "to": "IN_PRODUCTION", "revert": true },
    { "from": "IN_TRANSIT", "to": "SENT_TO_WAREHOUSE", "revert": true },
    { "from": "SENT_TO_STORE", "to": "IN_TRANSIT", "revert": true },
    { "from": "RECEIVED_IN_STORE", "to": "SENT_TO_STORE", "revert": true },
    { "from": "OUT_FOR_DELIVERY", "to": "RECEIVED_IN_STORE", "revert": true }
  ]
}');

COMMIT;
