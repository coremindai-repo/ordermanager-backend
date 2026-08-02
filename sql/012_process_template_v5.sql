-- Epic 5 — process template v5: declare which transitions notify.
--
-- Identical to v4 except that IN_PRODUCTION -> READY_TO_INVOICE now carries
-- "notifyEvent": "invoice_ready". That is the invoicing handoff: production finishes,
-- the accountant is told, they invoice manually in ZOHO (no integration — CLAUDE.md
-- §8), then move the order on to READY_TO_DELIVER themselves.
--
-- Putting the trigger in the template keeps the whole notification path config-driven:
--   which status fires an event  -> here
--   who receives that event      -> notification_recipients
-- Neither requires a code change for a client whose process differs.
--
-- No statuses added or removed, so no order can be stranded (CLAUDE.md §5).
--
-- REMINDER: templates are cached per worker instance — redeploy to take effect.

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

BEGIN TRANSACTION;

UPDATE process_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO process_templates (client_id, version, active, template_json)
VALUES (@clientId, 5, 1, N'{
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
      "orderTypes": ["customer"], "requiresAllLineItemsComplete": true,
      "notifyEvent": "invoice_ready" },
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
