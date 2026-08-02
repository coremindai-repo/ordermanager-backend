-- Epic 8 correction — production step template v3: SEMI_FINISHED is outsource-only.
--
-- v2 allowed both outsource and import to come back semi-finished. That is wrong: an
-- outsourcing supplier may do only part of the job, so the goods return needing factory
-- work — but an IMPORT always arrives complete, and factory-made items are never
-- "semi-finished" either (a part-built factory item is work in progress, sitting on a
-- production step, not a returned state).
--
-- So the only route into SEMI_FINISHED, and the only route out of it, is method=outsource.
--
--   Factory:    PENDING -[factory]-> CARPENTRY | POLISHING | UPHOLSTERY -> ... -> FINISHED
--   Import:     PENDING -[import]-> WITH_SUPPLIER -> FINISHED
--   Outsource:  PENDING -[outsource]-> WITH_SUPPLIER -> FINISHED
--                                                    -> SEMI_FINISHED -> {factory steps}
--
-- WITH_SUPPLIER -> FINISHED stays open to both, since either route can return complete
-- goods. Only the semi-finished branch narrows.
--
-- MIGRATION: v3 removes no statuses, but it does remove import's route into and out of
-- SEMI_FINISHED. An imported item sitting there would be stranded, so the guard below
-- refuses to run rather than trapping it.
--
-- REMINDER: templates are cached per worker instance — redeploy to take effect.

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

IF EXISTS (
    SELECT 1 FROM order_line_items
    WHERE method = 'import' AND current_status = 'SEMI_FINISHED'
)
BEGIN
    THROW 50003, 'Imported line items sit at SEMI_FINISHED — under v3 imports cannot be semi-finished and these would be stranded. Migrate them first.', 1;
END

BEGIN TRANSACTION;

UPDATE production_step_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO production_step_templates (client_id, version, active, template_json)
VALUES (@clientId, 3, 1, N'{
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

    { "from": "WITH_SUPPLIER", "to": "SEMI_FINISHED", "methods": ["outsource"] },
    { "from": "SEMI_FINISHED", "to": "CARPENTRY", "methods": ["outsource"] },
    { "from": "SEMI_FINISHED", "to": "POLISHING", "methods": ["outsource"] },
    { "from": "SEMI_FINISHED", "to": "UPHOLSTERY", "methods": ["outsource"] },

    { "from": "CARPENTRY", "to": "POLISHING" },
    { "from": "CARPENTRY", "to": "UPHOLSTERY" },
    { "from": "CARPENTRY", "to": "FINISHED" },
    { "from": "POLISHING", "to": "UPHOLSTERY" },
    { "from": "POLISHING", "to": "FINISHED" },
    { "from": "UPHOLSTERY", "to": "FINISHED" }
  ]
}');

COMMIT;
