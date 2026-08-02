-- Epic 4 — process template v2: gate leaving production on all line items finished.
--
-- An order ships as one unit: it only leaves the factory once EVERY line item on it
-- is complete, and the whole order then moves to a single store. v1 had no such gate,
-- so an order could reach post-production with items still in progress.
--
-- Templates are versioned, never edited in place — v1 is deactivated and kept for
-- history (existing orders' audit trail refers to the rules that applied at the time).
--
-- REMINDER: the app caches the active template per worker instance. This change does
-- not take effect until the Function App is redeployed (CLAUDE.md §5).

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

BEGIN TRANSACTION;

-- Deactivate first: the filtered unique index permits only one active row per client.
UPDATE process_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO process_templates (client_id, version, active, template_json)
VALUES (@clientId, 2, 1, N'{
  "initialStatus": "NEW",
  "statuses": [
    { "code": "NEW", "name": "New Order Capture" },
    { "code": "IN_PRODUCTION", "name": "In Production" },
    { "code": "POST_PRODUCTION", "name": "Post-Production" },
    { "code": "READY_TO_INVOICE", "name": "Ready to Invoice" },
    { "code": "READY_TO_DELIVER", "name": "Ready to Deliver" },
    { "code": "DELIVERED", "name": "Delivered" }
  ],
  "transitions": [
    { "from": "NEW", "to": "IN_PRODUCTION" },
    { "from": "IN_PRODUCTION", "to": "POST_PRODUCTION", "requiresAllLineItemsComplete": true },
    { "from": "POST_PRODUCTION", "to": "READY_TO_INVOICE" },
    { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER" },
    { "from": "READY_TO_DELIVER", "to": "DELIVERED" },
    { "from": "POST_PRODUCTION", "to": "IN_PRODUCTION", "revert": true }
  ]
}');

COMMIT;
