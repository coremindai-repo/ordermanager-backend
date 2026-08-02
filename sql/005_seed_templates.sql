-- Epic 2 — seed the pilot client's active templates.
--
-- Pilot client_id: c6c944a9-b531-4c21-a3fd-9a8d6df2b180 (matches the CLIENT_ID app setting).
--
-- `allowedRoles` is deliberately omitted from every transition below, which the
-- engine reads as "any authenticated role may perform this transition". Per-transition
-- role gating is supported (and unit-tested) but is not seeded, because the client
-- has not confirmed who performs each step — inventing that here would silently 403
-- real users. Add allowedRoles per transition once confirmed; it is a template edit
-- plus a redeploy, not a code change.
--
-- Reminder: templates are cached for the life of the worker instance. Editing these
-- rows requires a redeploy to take effect (CLAUDE.md §5).

-- Required: these tables carry filtered indexes, and INSERT against them fails
-- without it. Some clients default this OFF.
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @clientId UNIQUEIDENTIFIER = 'c6c944a9-b531-4c21-a3fd-9a8d6df2b180';

-- Main process (CLAUDE.md §4): New Order Capture → In Production → Post-Production
-- → Ready to Invoice → Ready to Deliver → Delivered, plus one explicit revert
-- allowance for rework sent back from post-production.
INSERT INTO process_templates (client_id, version, active, template_json)
VALUES (@clientId, 1, 1, N'{
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
    { "from": "IN_PRODUCTION", "to": "POST_PRODUCTION" },
    { "from": "POST_PRODUCTION", "to": "READY_TO_INVOICE" },
    { "from": "READY_TO_INVOICE", "to": "READY_TO_DELIVER" },
    { "from": "READY_TO_DELIVER", "to": "DELIVERED" },
    { "from": "POST_PRODUCTION", "to": "IN_PRODUCTION", "revert": true }
  ]
}');

-- Factory production steps (CLAUDE.md §4 example list). Items pick which steps they
-- require on the "This item will require" checklist, so an item may legitimately skip
-- a step it did not select — hence forward edges to every later step rather than a
-- strict chain. Which steps a given item must complete is enforced by its production
-- plan (Epic 4); this template defines which moves are structurally legal at all.
--
-- No `methods` filters are seeded: the engine supports restricting a transition to
-- specific methods (factory/outsource/import) and that is unit-tested, but the
-- outsource/import flows are Epic 6 and undefined, so nothing is fabricated here.
INSERT INTO production_step_templates (client_id, version, active, template_json)
VALUES (@clientId, 1, 1, N'{
  "initialStatus": "PENDING",
  "statuses": [
    { "code": "PENDING", "name": "Pending" },
    { "code": "CARPENTRY", "name": "Carpentry" },
    { "code": "POLISHING", "name": "Polishing" },
    { "code": "UPHOLSTERY", "name": "Upholstery" },
    { "code": "FINISHED", "name": "Finished" }
  ],
  "transitions": [
    { "from": "PENDING", "to": "CARPENTRY" },
    { "from": "PENDING", "to": "POLISHING" },
    { "from": "PENDING", "to": "UPHOLSTERY" },
    { "from": "CARPENTRY", "to": "POLISHING" },
    { "from": "CARPENTRY", "to": "UPHOLSTERY" },
    { "from": "CARPENTRY", "to": "FINISHED" },
    { "from": "POLISHING", "to": "UPHOLSTERY" },
    { "from": "POLISHING", "to": "FINISHED" },
    { "from": "UPHOLSTERY", "to": "FINISHED" }
  ]
}');
