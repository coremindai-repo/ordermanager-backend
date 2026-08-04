-- Process template v7 — NEW -> IN_PRODUCTION becomes factory_supervisor's, and fires
-- automatically instead of sitting unreachable.
--
-- Mobile traced a real gap: nothing anywhere ever called NEW -> IN_PRODUCTION. No screen,
-- no automatic trigger. Every order sat at NEW forever, which blocked the entire
-- downstream flow (supervisorLegalNextStatuses/managerLegalNextStatuses correctly
-- returned nothing for a status neither recognised as actionable).
--
-- v6's allowedRoles on this edge — salesperson, company_manager — was never actually
-- exercised by anything and, on inspection, never should have been: nothing in this file
-- explained or confirmed it the way the KEEP_IN_FACTORY/dispatch rows are, unlike every
-- other unusual-looking role assignment in this template's history.
--
-- CONFIRMED WITH THE CLIENT: the real ownership sequence is salesperson (order capture)
-- -> supervisor (once submitted, through complete/leaves factory) -> invoice raised for
-- customer orders at that handoff -> store manager from there. NEW -> IN_PRODUCTION marks
-- the sales-to-production handoff, which is the supervisor's domain starting the moment
-- the order lands with them — not the salesperson's to advance.
--
-- SetProductionPlan.cs now fires this transition itself, through the same validator and
-- writing the same order_status_history row as a manual transition, the first time any
-- line item on the order gets a plan set (idempotent — a no-op once the order has moved
-- past NEW, so later re-plans and semi-finished re-entries do not attempt it again).
-- That is why the edge now belongs to factory_supervisor: that is the caller whose action
-- actually triggers it, symmetric with how the order already leaves production only once
-- every line item is complete (requiresAllLineItemsComplete) — entering production is the
-- first item starting; leaving is the last one finishing.
--
-- MIGRATION: no statuses added or removed, so nothing can be stranded going forward.
-- Existing orders already sitting at NEW under the old, unreachable rule are a separate,
-- explicitly flagged question — this script does not touch existing order rows.
--
-- REMINDER: templates are cached per worker instance — redeploy to take effect.

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

BEGIN TRANSACTION;

UPDATE process_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO process_templates (client_id, version, active, template_json)
VALUES (@clientId, 7, 1, N'{
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
    { "from": "NEW", "to": "IN_PRODUCTION",
      "allowedRoles": ["factory_supervisor"] },

    { "from": "IN_PRODUCTION", "to": "READY_TO_INVOICE",
      "orderTypes": ["customer"], "requiresAllLineItemsComplete": true,
      "notifyEvent": "invoice_ready",
      "allowedRoles": ["factory_supervisor"] },
    { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER",
      "orderTypes": ["customer"],
      "allowedRoles": ["store_manager", "company_manager"] },

    { "from": "READY_TO_DELIVER", "to": "KEEP_IN_FACTORY",
      "orderTypes": ["customer"],
      "allowedRoles": ["factory_supervisor"] },
    { "from": "READY_TO_DELIVER", "to": "SENT_TO_WAREHOUSE",
      "orderTypes": ["customer"],
      "allowedRoles": ["factory_supervisor"] },

    { "from": "IN_PRODUCTION", "to": "KEEP_IN_FACTORY",
      "orderTypes": ["stock"], "requiresAllLineItemsComplete": true,
      "allowedRoles": ["factory_supervisor"] },
    { "from": "IN_PRODUCTION", "to": "SENT_TO_WAREHOUSE",
      "orderTypes": ["stock"], "requiresAllLineItemsComplete": true,
      "allowedRoles": ["factory_supervisor"] },

    { "from": "KEEP_IN_FACTORY", "to": "SENT_TO_WAREHOUSE",
      "allowedRoles": ["factory_supervisor"] },
    { "from": "KEEP_IN_FACTORY", "to": "IN_TRANSIT",
      "requiresDestinationStore": true,
      "allowedRoles": ["factory_supervisor"] },
    { "from": "SENT_TO_WAREHOUSE", "to": "IN_TRANSIT",
      "requiresDestinationStore": true,
      "allowedRoles": ["factory_supervisor"] },

    { "from": "IN_TRANSIT", "to": "SENT_TO_STORE",
      "allowedRoles": ["store_manager", "company_manager"] },
    { "from": "SENT_TO_STORE", "to": "RECEIVED_IN_STORE",
      "allowedRoles": ["store_manager", "company_manager"] },
    { "from": "RECEIVED_IN_STORE", "to": "OUT_FOR_DELIVERY",
      "allowedRoles": ["store_manager", "company_manager"] },
    { "from": "OUT_FOR_DELIVERY", "to": "DELIVERED",
      "allowedRoles": ["store_manager", "company_manager"] },

    { "from": "KEEP_IN_FACTORY", "to": "IN_PRODUCTION", "revert": true,
      "allowedRoles": ["factory_supervisor"] },
    { "from": "SENT_TO_WAREHOUSE", "to": "IN_PRODUCTION", "revert": true,
      "allowedRoles": ["factory_supervisor"] },
    { "from": "IN_TRANSIT", "to": "SENT_TO_WAREHOUSE", "revert": true,
      "allowedRoles": ["factory_supervisor"] },
    { "from": "SENT_TO_STORE", "to": "IN_TRANSIT", "revert": true,
      "allowedRoles": ["store_manager", "company_manager"] },
    { "from": "RECEIVED_IN_STORE", "to": "SENT_TO_STORE", "revert": true,
      "allowedRoles": ["store_manager", "company_manager"] },
    { "from": "OUT_FOR_DELIVERY", "to": "RECEIVED_IN_STORE", "revert": true,
      "allowedRoles": ["store_manager", "company_manager"] }
  ]
}');

COMMIT;
