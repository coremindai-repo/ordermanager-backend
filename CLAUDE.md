# Order Management — Backend (Azure)

Repository: `order-management-backend`
Consumer: Claude Code, operating under standing rules below.
Companion repo: `order-management-mobile` (React Native). Shared contract:
`/docs/API-INTERFACE-CONTRACT.md` (copy of the file with the same name in
the mobile repo — keep them identical).

## 1. Purpose

Backend for a manufacturing order management platform, built as a
configurable pilot for one client, but architected so the process logic is
driven by data, not code, so it can be licensed to future clients without
code changes.

Roles served: Salesperson, Factory Supervisor, Store Manager, Company
Manager (Company Manager also sees everything Store Manager sees, plus
outsourcing/import).

## 2. Standing rules for Claude Code

- This file and everything under `/docs` are authoritative. If a build
  decision conflicts with them, stop and ask rather than improvising.
- Write tests for the workflow engine (status transition validation) before
  building screens/endpoints that depend on it.
- Do not touch the mobile repository. If a change here requires a change to
  `API-INTERFACE-CONTRACT.md`, flag it and wait for confirmation before
  changing the contract — the mobile team builds against it independently.
- Prefer the simplest implementation that satisfies the spec. This is a
  pilot for a small internal user group — do not add infrastructure
  (message queues, orchestration engines, gateways) the current scope
  doesn't need. Specific decisions below are deliberate, not oversights.

## 3. Architecture decisions (pilot scope)

| Area | Decision | Why |
|---|---|---|
| Compute | Azure Functions, HTTP-triggered, plain (no Durable Functions) | No flow here needs stateful multi-step orchestration; the one multi-step case (order submit → SOHO call → DB write) is a single function with a try/compensate block |
| Async messaging | None (no Service Bus) | Procurement/outsourcing/import are human-driven manual steps (WhatsApp, phone) — nothing to decouple with a queue at this scale |
| Data store | Azure SQL only | Cosmos DB is deferred to Phase 2 (chat/RAG); nothing in this scope needs a document store |
| Notifications | Synchronous push call (Azure Notification Hubs → FCM/APNs) fired inline after a status-changing write succeeds | Push-only requirement; no queue needed at this volume |
| Real-time updates | None — REST GET only, client-initiated refresh | Explicit pilot constraint: no WebSocket/SignalR |
| Auth | Custom Users table + JWT (HS256, 12h expiry) | No IAM in pilot; small trusted user group |
| API exposure | Direct HTTPS calls to Functions, no API Management | Deferred until IAM/APIM integration phase |
| Language/runtime | **.NET 10, isolated worker model, C#, on Azure Functions runtime 4.x** | Team standard is .NET/C#; .NET 10 is the current LTS (GA on Azure Functions, isolated worker only) with support through Nov 2028, vs. .NET 8 reaching end-of-support Nov 2026 |
| Hosting plan | **Flex Consumption** | .NET 10 is not supported on the Linux Consumption plan — Flex Consumption (or a Windows-based plan) is required |
| Data access | EF Core or Dapper against Azure SQL (confirm team preference) | Either is fine for this scope; pick one and stay consistent across epics |
| Password hashing | BCrypt.Net | .NET-native equivalent of bcrypt |
| JWT | `System.IdentityModel.Tokens.Jwt` / `Microsoft.AspNetCore.Authentication.JwtBearer` | Standard .NET JWT issuance/validation |

If pilot volume or client count grows and any of these becomes a real
bottleneck (e.g., procurement genuinely needs supplier API automation),
revisit — the workflow engine's config-driven design means the process
logic doesn't need to change, only the plumbing around it.

## 4. Data model (Azure SQL)

Core tables — adjust field lists during build, but keep this table set:

- **users** — id, username, password_hash, email, first_name, last_name,
  mobile_no, active
- **user_roles** — user_id, role (`salesperson`, `factory_supervisor`,
  `store_manager`, `company_manager`)
- **stores** — id, name, location, active (seed: Kochi, Bangalore — this is
  reference data, not a client template; adding a store is a row insert)
- **process_templates** — id, client_id, version, active, template_json
  (the ordered list of main-process stages: New Order Capture → In
  Production → Post-Production → Ready to Invoice → Ready to Deliver →
  Delivered, with allowed transitions)
- **production_step_templates** — id, client_id, version, active,
  template_json (the client-specific factory stage list, e.g. Carpentry,
  Polishing, Upholstery, Finished)
Order number scheme (fixed — do not change without agreeing it with the
mobile team, staff read and search on these):

| Order type | Format | Example |
|---|---|---|
| Customer | `CUS-` + SOHO sales order number | `CUS-4471` (stubbed: `CUS-STUB471203`) |
| Stock | `STK-{yyMM}-{sequence:D4}` | `STK-2608-0042` |

The stock sequence comes from the SQL `seq_stock_order_number` sequence. It is
continuous and never resets — the `yyMM` segment is for human readability only,
so uniqueness never depends on the date and concurrent submissions cannot
collide. `D4` is a minimum width, not a cap.

- **orders** — id, order_number, order_type (`customer`|`stock`), soho_order_ref
  (nullable), current_status, store_id (destination), created_by,
  created_at, updated_at
- **order_line_items** — id, order_id, item_name, description,
  current_status, method (`factory`|`outsource`|`import`), current_step,
  created_at, updated_at
- **order_line_item_steps** — id, line_item_id, step_name, status
  (`pending`|`started`|`complete`), assigned_names (json array),
  photo_urls (json array), started_at, completed_at
- **order_status_history** / **line_item_status_history** — append-only,
  every transition ever made, with user_id and timestamp. This is the table
  that makes future reporting possible — do not skip or thin this out for
  convenience.
- **billing_shipping_details** — order_id, bill_to json, ship_to json
- **materials** — line_item_id, material details (as captured on the
  "Add Material" screen)
- **raw_material_requests** — id, requested_by, items json, status,
  supplier json (nullable), timestamps
- **outsourcing_requests** — id, requested_by, items json, status,
  supplier json (nullable), timestamps
- **inventory** — id, product_name, status (`finished`|`semi_finished`),
  location, quantity
- **notifications_log** — id, user_id, type, order_id, line_item_id, title,
  body, sent_at
- **device_tokens** — user_id, platform, push_token, updated_at

## 5. Workflow engine

One generic transition endpoint per entity (order, line item — see
contract §4–5), validated against whichever template applies:

- Order-level transitions validate against `process_templates`.
- Line-item production transitions validate against
  `production_step_templates` plus the item's chosen method
  (factory/outsource/import).
- An illegal transition (skipping a required stage, moving backward without
  an explicit "revert" allowance) returns `409` — write this validation
  logic once, and write its tests first, since every status-changing
  endpoint in the system depends on it.
- Every successful transition writes to the relevant `*_status_history`
  table and, where the transition matches a notification-worthy event
  (invoice ready, raw materials received, item assigned), fires a push via
  §7.

### Template changes require a redeploy — this is deliberate

The active template is loaded once per worker instance and cached for the
life of that process. **Editing a template row in SQL does not take effect
until the Function App is redeployed.** This is intended behaviour, not a
missing cache-invalidation feature: template changes go through client
approval and a dev-initiated redeploy, and the redeploy is what cycles the
instances holding the cache. Do not "fix" this by adding a TTL or an
invalidation endpoint without raising it first.

The corollary is the part worth remembering: because the cache is per
*instance* and Flex Consumption scales instances in and out, editing a
template **without** redeploying leaves already-running instances serving
the old rules while newly-started instances serve the new ones — the same
request can then be validated differently depending on which instance
handles it. Always pair a template edit with a redeploy.

Template shape (`template_json`): every legal move is an explicit
`{ "from", "to" }` edge. Skipping a stage is illegal because no edge
exists; a backward move requires an edge marked `"revert": true`. Optional
`allowedRoles` restricts who may perform a transition — **omitted or empty
means any authenticated role**, not "deny all". Optional `methods`
restricts an edge to specific line-item methods; omitted means all methods.

## 6. Endpoints

Full contract in `/docs/API-INTERFACE-CONTRACT.md` — build to that
document exactly; it's shared with the mobile team and is the integration
point between the two repos.

## 7. Notifications

On any status-changing write that matches a notification-worthy transition:
look up the target user(s)' `device_tokens`, call Azure Notification Hubs
synchronously, log the attempt to `notifications_log` regardless of
delivery success (delivery failure is not fatal — the user's refresh
button is the fallback). No email/SMS provider needed anywhere in this repo.

## 8. External integrations

- **SOHO API** — called once, synchronously, during order submission for
  customer orders, to obtain the Sales Order number. Treat as a hard
  dependency; if it's down, order submission should fail cleanly (not
  silently create an order without a valid SOHO reference).

  > ### ⚠ SOHO IS CURRENTLY A STUB — NOT A FINISHED INTEGRATION
  >
  > The client has not yet provided their SOHO API. `ISohoClient`
  > (`Lib/Soho/`) defines the contract; the only implementations today are:
  >
  > - `StubSohoClient` — returns invented placeholder numbers. Active only
  >   when `SOHO_MODE=stub`. Placeholders are prefixed `STUB`, so stubbed
  >   orders read as e.g. `CUS-STUB471203` and are obvious in the database.
  > - `UnconfiguredSohoClient` — the default. Customer submissions fail with
  >   `503 SOHO_UNAVAILABLE`. Deliberately the default so a misconfigured
  >   deploy cannot quietly mint fake references into real client data.
  >
  > **Before go-live:** add a real `ISohoClient` implementation and remove
  > `SOHO_MODE=stub` from the Function App settings. Nothing outside
  > `Lib/Soho/` should need to change — the endpoint, compensation and
  > numbering all sit behind the interface.
  >
  > Also revisit then: customer order numbers are `CUS-` + SOHO's number
  > verbatim. If SOHO's real numbers carry their own prefix (e.g. `SO-4471`)
  > the result doubles up as `CUS-SO-4471`. Left as-is rather than stripping
  > characters on a guess about a format nobody has seen.
- **ZOHO invoicing** — no integration. Invoice generation is manual outside
  the app (confirmed from the client wireframes); the app only needs to
  notify the accountant that an order is ready to invoice and later accept
  a manual "invoice generated" status update.
- **Speech-to-text** (voice order instructions) — **flagged, not yet
  confirmed in scope.** If confirmed, this is an Azure Speech call from the
  mobile app directly (not proxied through this backend) — the backend
  only ever receives and stores the resulting text. Do not build a backend
  proxy for this unless told otherwise.

## 9. Build sequencing (epics)

1. **Epic 1 — Foundations:** Users/roles/auth (login, JWT), stores lookup,
   base Function App skeleton, error-shape middleware, CI to Azure.
2. **Epic 2 — Workflow engine core:** process_templates,
   production_step_templates schema + loader, generic transition endpoint
   + validation, status_history tables, unit tests for every
   legal/illegal transition case before moving on.
3. **Epic 3 — Order capture:** POST /orders (incl. SOHO call for customer
   orders), order/line-item CRUD, billing/shipping, materials.
4. **Epic 4 — Factory production flow:** production plan, step updates,
   photo attachment storage (Azure Blob), post-production routing to
   stores.
5. **Epic 5 — Store manager flow:** invoicing handoff, item logistics
   status updates, raw material requests.
6. **Epic 6 — Outsourcing/import flow.**
7. **Epic 7 — Notifications:** Notification Hubs integration, device token
   registration, notifications_log, wiring into Epics 3–6's transitions.
8. **Epic 8 — Inventory search, order history, dashboards (read APIs).**

Ask before reordering or collapsing epics — the sequencing exists so the
workflow engine (Epic 2) is solid before anything depends on it.
