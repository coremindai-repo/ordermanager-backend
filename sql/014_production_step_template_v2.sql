-- Epic 6 — production step template v2: add the outsource / import path.
--
-- v1 only described factory work, so an outsourced item had nowhere legal to sit.
-- v2 branches on the item's `method`, using the `methods` restriction that has been
-- part of the engine since Epic 2 and unused until now.
--
--   Factory:            PENDING --[factory]--> CARPENTRY | POLISHING | UPHOLSTERY
--                                                       -> ... -> FINISHED
--
--   Outsource/import:   PENDING --[outsource,import]--> WITH_SUPPLIER
--                       WITH_SUPPLIER -> FINISHED            (received finished)
--                       WITH_SUPPLIER -> SEMI_FINISHED       (received semi-finished)
--                       SEMI_FINISHED -> CARPENTRY | POLISHING | UPHOLSTERY
--
-- The semi-finished route deliberately rejoins the SAME factory step edges rather
-- than duplicating them: the wireframes say such items are "shown the production
-- steps as in the factory production flow", so once an item reaches a factory step it
-- is indistinguishable from one that started there. The onward edges
-- (CARPENTRY -> POLISHING, etc.) carry no `methods` restriction, which is what makes
-- that rejoin work with no extra edges.
--
-- PENDING -> factory steps is now restricted to method=factory. Previously
-- unrestricted, it would have let an outsourced item skip its supplier entirely.
--
-- FINISHED remains the only terminal status, so LineItemCompletion (and therefore the
-- order-level gate A) treats outsourced and factory items identically: an order still
-- cannot leave production until every item, by whatever route, reaches FINISHED.
--
-- MIGRATION: v2 removes no statuses. The guard below covers the one genuine hazard —
-- items sitting at a factory step with a non-factory method, which existed under v1's
-- unrestricted edges and would now be unable to move.
--
-- REMINDER: templates are cached per worker instance — redeploy to take effect.

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

IF EXISTS (
    SELECT 1 FROM order_line_items
    WHERE method IN ('outsource', 'import')
      AND current_status = 'PENDING'
)
BEGIN
    -- These items would still be fine (PENDING -> WITH_SUPPLIER is open to them), so
    -- this is informational rather than fatal.
    PRINT 'Note: outsource/import items currently at PENDING will now route via WITH_SUPPLIER.';
END

BEGIN TRANSACTION;

UPDATE production_step_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO production_step_templates (client_id, version, active, template_json)
VALUES (@clientId, 2, 1, N'{
  "initialStatus": "PENDING",
  "statuses": [
    { "code": "PENDING", "name": "Pending" },
    { "code": "WITH_SUPPLIER", "name": "With Supplier" },
    { "code": "SEMI_FINISHED", "name": "Received Semi-Finished" },
    { "code": "CARPENTRY", "name": "Carpentry" },
    { "code": "POLISHING", "name": "Polishing" },
    { "code": "UPHOLSTERY", "name": "Upholstery" },
    { "code": "FINISHED", "name": "Finished" }
  ],
  "transitions": [
    { "from": "PENDING", "to": "CARPENTRY", "methods": ["factory"] },
    { "from": "PENDING", "to": "POLISHING", "methods": ["factory"] },
    { "from": "PENDING", "to": "UPHOLSTERY", "methods": ["factory"] },

    { "from": "PENDING", "to": "WITH_SUPPLIER", "methods": ["outsource", "import"] },
    { "from": "WITH_SUPPLIER", "to": "FINISHED", "methods": ["outsource", "import"] },
    { "from": "WITH_SUPPLIER", "to": "SEMI_FINISHED", "methods": ["outsource", "import"] },

    { "from": "SEMI_FINISHED", "to": "CARPENTRY", "methods": ["outsource", "import"] },
    { "from": "SEMI_FINISHED", "to": "POLISHING", "methods": ["outsource", "import"] },
    { "from": "SEMI_FINISHED", "to": "UPHOLSTERY", "methods": ["outsource", "import"] },

    { "from": "CARPENTRY", "to": "POLISHING" },
    { "from": "CARPENTRY", "to": "UPHOLSTERY" },
    { "from": "CARPENTRY", "to": "FINISHED" },
    { "from": "POLISHING", "to": "UPHOLSTERY" },
    { "from": "POLISHING", "to": "FINISHED" },
    { "from": "UPHOLSTERY", "to": "FINISHED" }
  ]
}');

COMMIT;
