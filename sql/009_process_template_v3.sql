-- Epic 4 — process template v3: expand POST_PRODUCTION into the logistics chain.
--
-- v2 collapsed everything after production into a single POST_PRODUCTION status. The
-- wireframes show a finer-grained chain, so v3 replaces it with:
--
--   KEEP_IN_FACTORY, SENT_TO_WAREHOUSE, IN_TRANSIT, SENT_TO_STORE,
--   RECEIVED_IN_STORE, OUT_FOR_DELIVERY
--
-- Flow:
--
--   NEW -> IN_PRODUCTION --[all line items complete]--> KEEP_IN_FACTORY
--                        \                          \-> SENT_TO_WAREHOUSE
--                         \                             |
--   KEEP_IN_FACTORY -> SENT_TO_WAREHOUSE                |
--   KEEP_IN_FACTORY --[destination store set]--> IN_TRANSIT <-+
--   SENT_TO_WAREHOUSE --[destination store set]--> IN_TRANSIT
--   IN_TRANSIT -> SENT_TO_STORE -> RECEIVED_IN_STORE
--   RECEIVED_IN_STORE -> READY_TO_INVOICE -> READY_TO_DELIVER -> OUT_FOR_DELIVERY
--   RECEIVED_IN_STORE -> OUT_FOR_DELIVERY        (straight out, no invoicing step yet)
--   OUT_FOR_DELIVERY -> DELIVERED
--
-- Statuses are deliberately store-agnostic: "In Transit", never "Sent to Kochi".
-- Which store is carried by orders.store_id, so adding a third store is a row insert
-- rather than a status explosion. The requiresDestinationStore gate is what stops an
-- order moving towards a store that was never chosen.
--
-- NOTE ON READY_TO_INVOICE / READY_TO_DELIVER: these are accountant-driven and, per
-- the wireframes, independent of physical location. A single current_status column
-- cannot express two genuinely parallel tracks, so they are modelled here as an
-- optional route between RECEIVED_IN_STORE and OUT_FOR_DELIVERY — an order may pass
-- through invoicing or go straight out. If they must be able to advance while the
-- goods are elsewhere (e.g. READY_TO_INVOICE while still IN_TRANSIT), that needs a
-- separate invoice_status column, not a template change. Flagged, not assumed.
--
-- MIGRATION: v3 removes POST_PRODUCTION. Any order sitting in that status would be
-- stranded — the validator would report UnknownCurrentStatus and the order could not
-- move at all. The orders table is empty at the time of writing, so no backfill is
-- included. Any future template change that removes a status MUST migrate affected
-- orders in the same script.
--
-- REMINDER: templates are cached per worker instance. This does not take effect
-- until the Function App is redeployed (CLAUDE.md §5).

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

-- Guard: refuse to run if any order is parked in a status v3 drops.
IF EXISTS (SELECT 1 FROM orders WHERE current_status = 'POST_PRODUCTION')
BEGIN
    THROW 50001, 'Orders still in POST_PRODUCTION — migrate them before activating v3, or they will be stranded.', 1;
END

BEGIN TRANSACTION;

UPDATE process_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO process_templates (client_id, version, active, template_json)
VALUES (@clientId, 3, 1, N'{
  "initialStatus": "NEW",
  "statuses": [
    { "code": "NEW", "name": "New Order Capture" },
    { "code": "IN_PRODUCTION", "name": "In Production" },
    { "code": "KEEP_IN_FACTORY", "name": "Keep in Factory" },
    { "code": "SENT_TO_WAREHOUSE", "name": "Sent to Warehouse" },
    { "code": "IN_TRANSIT", "name": "In Transit" },
    { "code": "SENT_TO_STORE", "name": "Sent to Store" },
    { "code": "RECEIVED_IN_STORE", "name": "Received in Store" },
    { "code": "READY_TO_INVOICE", "name": "Ready to Invoice" },
    { "code": "READY_TO_DELIVER", "name": "Ready to Deliver" },
    { "code": "OUT_FOR_DELIVERY", "name": "Out for Delivery" },
    { "code": "DELIVERED", "name": "Delivered" }
  ],
  "transitions": [
    { "from": "NEW", "to": "IN_PRODUCTION" },

    { "from": "IN_PRODUCTION", "to": "KEEP_IN_FACTORY", "requiresAllLineItemsComplete": true },
    { "from": "IN_PRODUCTION", "to": "SENT_TO_WAREHOUSE", "requiresAllLineItemsComplete": true },

    { "from": "KEEP_IN_FACTORY", "to": "SENT_TO_WAREHOUSE" },
    { "from": "KEEP_IN_FACTORY", "to": "IN_TRANSIT", "requiresDestinationStore": true },
    { "from": "SENT_TO_WAREHOUSE", "to": "IN_TRANSIT", "requiresDestinationStore": true },

    { "from": "IN_TRANSIT", "to": "SENT_TO_STORE" },
    { "from": "SENT_TO_STORE", "to": "RECEIVED_IN_STORE" },

    { "from": "RECEIVED_IN_STORE", "to": "READY_TO_INVOICE" },
    { "from": "RECEIVED_IN_STORE", "to": "OUT_FOR_DELIVERY" },
    { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER" },
    { "from": "READY_TO_DELIVER", "to": "OUT_FOR_DELIVERY" },

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
