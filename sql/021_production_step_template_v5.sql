-- Production step template v5: mark which statuses are selectable production steps.
--
-- BUG FIX. GET /api/production-steps-template returned every status except the initial
-- one, so the "this item will require" checklist offered WITH_SUPPLIER, SEMI_FINISHED
-- and FINISHED alongside the real work steps — and POST .../production-plan ACCEPTED
-- them. A factory item could be planned with "With Supplier" as step 1, creating a
-- meaningless work item a supervisor could then mark complete.
--
-- Those four are lifecycle states the system sets, not work anyone performs:
--   PENDING         the starting state
--   WITH_SUPPLIER   set when an outsourcing request is placed
--   SEMI_FINISHED   set when a request is received semi-finished
--   FINISHED        reached by a line-item transition
--
-- Only CARPENTRY, POLISHING and UPHOLSTERY are genuine units of factory work, so only
-- those carry "selectableAsStep": true.
--
-- The flag defaults to FALSE in the parser. A future template that forgets it yields an
-- empty checklist — visibly broken — rather than silently reverting to offering
-- lifecycle statuses as though they were work.
--
-- No statuses added or removed, and no transition changed, so nothing can be stranded.
-- Items already planned with a lifecycle status as a step (only possible in dev) keep
-- those step rows; the guard below reports them rather than failing, since they are
-- harmless once no more can be created.
--
-- REMINDER: templates are cached per worker instance — redeploy to take effect.

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

IF EXISTS (
    SELECT 1 FROM order_line_item_steps
    WHERE step_name IN ('PENDING', 'WITH_SUPPLIER', 'SEMI_FINISHED', 'FINISHED')
)
BEGIN
    PRINT 'Note: existing step rows name a lifecycle status. These were created before v5 and should be reviewed:';
    SELECT line_item_id, step_name, status
    FROM order_line_item_steps
    WHERE step_name IN ('PENDING', 'WITH_SUPPLIER', 'SEMI_FINISHED', 'FINISHED');
END

BEGIN TRANSACTION;

UPDATE production_step_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO production_step_templates (client_id, version, active, template_json)
VALUES (@clientId, 5, 1, N'{
  "initialStatus": "PENDING",
  "statuses": [
    { "code": "PENDING", "name": "Pending" },
    { "code": "WITH_SUPPLIER", "name": "With Supplier" },
    { "code": "SEMI_FINISHED", "name": "Received Semi-Finished" },
    { "code": "CARPENTRY", "name": "Carpentry", "selectableAsStep": true },
    { "code": "POLISHING", "name": "Polishing", "selectableAsStep": true },
    { "code": "UPHOLSTERY", "name": "Upholstery", "selectableAsStep": true },
    { "code": "FINISHED", "name": "Finished" }
  ],
  "transitions": [
    { "from": "PENDING", "to": "CARPENTRY", "methods": ["factory"],
      "allowedRoles": ["factory_supervisor"] },
    { "from": "PENDING", "to": "POLISHING", "methods": ["factory"],
      "allowedRoles": ["factory_supervisor"] },
    { "from": "PENDING", "to": "UPHOLSTERY", "methods": ["factory"],
      "allowedRoles": ["factory_supervisor"] },

    { "from": "PENDING", "to": "WITH_SUPPLIER", "methods": ["outsource", "import"],
      "allowedRoles": ["company_manager"] },
    { "from": "WITH_SUPPLIER", "to": "FINISHED", "methods": ["outsource", "import"],
      "allowedRoles": ["company_manager"] },
    { "from": "WITH_SUPPLIER", "to": "SEMI_FINISHED", "methods": ["outsource"],
      "allowedRoles": ["company_manager"] },

    { "from": "SEMI_FINISHED", "to": "CARPENTRY", "methods": ["outsource"],
      "allowedRoles": ["factory_supervisor"] },
    { "from": "SEMI_FINISHED", "to": "POLISHING", "methods": ["outsource"],
      "allowedRoles": ["factory_supervisor"] },
    { "from": "SEMI_FINISHED", "to": "UPHOLSTERY", "methods": ["outsource"],
      "allowedRoles": ["factory_supervisor"] },

    { "from": "CARPENTRY", "to": "POLISHING", "allowedRoles": ["factory_supervisor"] },
    { "from": "CARPENTRY", "to": "UPHOLSTERY", "allowedRoles": ["factory_supervisor"] },
    { "from": "CARPENTRY", "to": "FINISHED", "allowedRoles": ["factory_supervisor"] },
    { "from": "POLISHING", "to": "UPHOLSTERY", "allowedRoles": ["factory_supervisor"] },
    { "from": "POLISHING", "to": "FINISHED", "allowedRoles": ["factory_supervisor"] },
    { "from": "UPHOLSTERY", "to": "FINISHED", "allowedRoles": ["factory_supervisor"] }
  ]
}');

COMMIT;
