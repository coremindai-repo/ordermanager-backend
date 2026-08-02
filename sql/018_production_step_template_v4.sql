-- Role gating — production step template v4.
--
-- Factory step transitions are factory_supervisor's, per the confirmed mapping.
--
-- ⚠ THE SUPPLIER EDGES ARE company_manager, NOT factory_supervisor, AND THAT IS
-- DELIBERATE. Those line-item moves are not performed by hand — they are driven by the
-- outsourcing request endpoints, which are company_manager's (contract §3). The
-- validator runs with the *requester's* roles, and on refusal AdvanceLineItemsAsync
-- skips the item and logs rather than failing the request. So gating these to
-- factory_supervisor would mean a company_manager receiving a request leaves every
-- linked item behind at its old status, with a warning nobody reads and a request that
-- looks successful.
--
-- If outsourcing is ever handed to another role, change BOTH the endpoint guard in
-- Functions/OutsourcingRequests.cs and these three edges together.
--
-- Once a semi-finished item rejoins the factory steps it is factory_supervisor's again,
-- which is why SEMI_FINISHED -> {step} carries factory_supervisor.
--
-- MIGRATION: no statuses added or removed.
--
-- REMINDER: templates are cached per worker instance — redeploy to take effect.

SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

BEGIN TRANSACTION;

UPDATE production_step_templates SET active = 0 WHERE client_id = @clientId AND active = 1;

INSERT INTO production_step_templates (client_id, version, active, template_json)
VALUES (@clientId, 4, 1, N'{
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
