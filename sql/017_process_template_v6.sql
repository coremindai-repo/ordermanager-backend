-- Role gating — process template v6.
--
-- Closes the gap that has been open since Epic 2: the engine has supported
-- `allowedRoles` all along, but no template set it, so any authenticated user could
-- perform any legal transition. This applies the client's confirmed mapping.
--
--   NEW -> IN_PRODUCTION                        salesperson, company_manager
--   Leaving production (both branches)          factory_supervisor
--   READY_TO_INVOICE -> READY_TO_DELIVER        store_manager, company_manager
--   Dispatch (-> IN_TRANSIT)                    factory_supervisor
--   Store-side movements                        store_manager, company_manager
--   Reverts                                     same as the forward move they undo
--
-- Two transitions were absent from the supplied mapping and were inferred as
-- factory_supervisor when this version was written. Both have since been CONFIRMED
-- correct by the client:
--
--   KEEP_IN_FACTORY -> SENT_TO_WAREHOUSE
--     A factory-floor movement.
--
--   READY_TO_DELIVER -> KEEP_IN_FACTORY / SENT_TO_WAREHOUSE
--     The physical dispatch decision once invoicing clears — NOT a revert of the
--     invoicing step. The factory supervisor owns that call, consistent with the other
--     dispatch transitions.
--
-- Recorded because the reasoning is not obvious from the edges alone: these two sit
-- immediately after store-side actions, so factory_supervisor looks anomalous until you
-- see that both are goods movements at the factory end.
--
-- MIGRATION: no statuses added or removed, so nothing can be stranded. But note this
-- version can strand *people*: a user whose role does not appear on any transition can
-- no longer move orders at all. The seeded devadmin holds only company_manager and can
-- therefore no longer perform factory_supervisor transitions — intended, but it will
-- change what a single test account can drive end to end.
--
-- REMINDER: templates are cached per worker instance — redeploy to take effect.

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

BEGIN TRANSACTION;

UPDATE process_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO process_templates (client_id, version, active, template_json)
VALUES (@clientId, 6, 1, N'{
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
      "allowedRoles": ["salesperson", "company_manager"] },

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
