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
- **Any log line that must be visible outside Azure Monitor — startup
  diagnostics, silent-failure warnings — must write to stdout, not just
  `ILogger`.** `host.json` sets `telemetryMode: OpenTelemetry`, under which
  worker `ILogger` output is routed to Azure Monitor and does **not** appear
  in the local `func start` console or the Azure log stream. Logs emitted
  before the worker connects to the host (anything in `Program.cs` ahead of
  `app.Run()`) are dropped entirely, since there is no channel yet.

  Routine per-request logging through `ILogger` is fine and correct — App
  Insights is where it belongs. This rule is for the small number of lines
  whose whole purpose is to be noticed: "this deployment is running a stub",
  "this notification reached nobody". Use `Console.WriteLine` **in addition
  to** `ILogger`, so the line lands in both places.

  Existing examples: the SOHO stub banner in `Program.cs`, and the
  zero-recipient warning in `NotificationService`. This has now been
  rediscovered twice (Epics 3 and 5) — if a warning you added seems not to
  fire, check this before assuming the code path is wrong.

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

### ⚠ OPEN: role gating is not yet applied to any transition

The engine supports per-transition `allowedRoles` and it is unit-tested, but **no
seeded template sets it**, so today *any authenticated user can perform any legal
transition*. This affects both Epic 2's order and line-item transitions and Epic 4's
production step updates and production-plan changes — none of them restrict by role.

This is a known gap awaiting the client's confirmed role-to-transition mapping, not
an oversight. When that mapping arrives it lands as a template update plus a redeploy
(a data change, not code). Until then, the mobile app hiding a control is the only
thing limiting who does what, and contract §3 is explicit that this is a UX
convenience rather than a security control. Do not treat the current behaviour as
the intended end state, and re-check it before go-live.

### Removing a status from a template requires migrating orders in the same script

The validator matches an order's `current_status` against the active template. Drop a
status that live orders are sitting in and those orders are **stranded** — every
transition returns `UnknownCurrentStatus` (409) and they cannot move at all, forwards
or backwards.

So any template version that removes a status must migrate affected orders in the same
script. `sql/009_process_template_v3.sql` shows the pattern: it refuses to run (`THROW`)
if any order still sits in the status being removed, rather than silently stranding
them. Copy that guard.

Template shape (`template_json`): every legal move is an explicit
`{ "from", "to" }` edge. Skipping a stage is illegal because no edge
exists; a backward move requires an edge marked `"revert": true`. Optional
`allowedRoles` restricts who may perform a transition — **omitted or empty
means any authenticated role**, not "deny all". Optional `methods`
restricts an edge to specific line-item methods; omitted means all methods. Optional
`orderTypes` is its order-level counterpart — it restricts an edge to `customer` or
`stock` orders, and is what branches the process so that invoicing (which happens
immediately after production) applies to customer orders only; stock orders route
straight into the logistics chain. Omitted means the edge applies to both.

Two order-level gates are also config, not code — which transitions carry them is a
template decision, so a client with a different process needs no code change:

| Flag | Effect | Error when unmet |
|---|---|---|
| `requiresAllLineItemsComplete` | Refuses the move unless every line item on the order has reached a terminal production status. An order ships as one unit. | `409 LINE_ITEMS_INCOMPLETE` |
| `requiresDestinationStore` | Refuses the move unless `orders.store_id` is set. Lets statuses stay generic (`IN_TRANSIT`, never `SENT_TO_KOCHI`) so adding a store is a row insert, not a status explosion. | `409 DESTINATION_STORE_REQUIRED` |

Both return 409 but are deliberately distinct from `ILLEGAL_TRANSITION`: the move is
legal in principle and the order simply isn't ready, so the app should prompt the user
to finish the remaining items or pick a store — not report "not allowed".

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

## 7a. Photo storage (production step attachments)

Photos are uploaded **by the device directly to Azure Blob storage**, never through
the Function — image bytes over a factory-floor connection would otherwise occupy a
Function execution for the whole upload.

Flow: the app asks for an upload URL, PUTs the bytes straight to Blob, then sends the
returned `blobPath` back on the step update call, which is where the upload is
validated.

| SAS | Scope | Lifetime | Why |
|---|---|---|---|
| Upload | One named blob, create+write only (`sp=cw`, `sr=b`) | **10 minutes** | Long enough for a large photo on a poor connection; short enough that a leaked URL is near-useless. Cannot read, list or delete anything. |
| Read | One named blob, read only (`sp=r`, `sr=b`) | **15 minutes** | Covers viewing a screen's photos including scrolling back, without being long enough to serve as a shareable or cacheable link. |

Only blob **paths** are stored in the database — never full URLs and never SAS
tokens. Read URLs are minted per response, so a URL captured from a response body or
a log stops working shortly afterwards.

Path layout: `{orderId}/{lineItemId}/{stepId}/{guid}.{ext}`.

Because the backend never sees the bytes, everything checkable is checked at
confirmation time (`PhotoPathValidator` plus a blob properties lookup): that the path
belongs to the step being updated, that the blob actually exists, that it is under
the size limit, and that its content type looks like an image. The allowed extension
list is shared between SAS issuing and confirmation so the two cannot drift.

## 7b. Notification routing — who gets told

"The accountant" in §8 is not a role: `user_roles` has only salesperson,
factory_supervisor, store_manager and company_manager. Rather than inventing a fifth
role, recipients are **data**, in `notification_recipients` — a row names an event type
plus either a role (everyone holding it) or one specific user, never both.

Seeded for the pilot: `invoice_ready` → store_manager + company_manager (contract §3
already lists "invoicing" under store_manager, and company_manager sees everything
store_manager does). Appointing a dedicated accountant later is a row insert.

Which transition fires which event is likewise config, not code — the `notifyEvent`
field on a template transition (see §5). So the whole path is data-driven:

| Question | Answered by |
|---|---|
| Which status change notifies? | `notifyEvent` on the transition, in `template_json` |
| Who receives it? | `notification_recipients` |

⚠ **Configurable routing means it can be configured to nobody.** If an event type has
no active recipient rows, the notification silently reaches no one while everything
else still reports success. The service logs a warning to both the logger and stdout
in that case — deliberately noisy, because this failure is otherwise invisible.

⚠ **No push is sent yet.** Epic 5 records rows in `notifications_log` with
`dispatched_at` NULL; Azure Notification Hubs is Epic 7 and replaces
`NotificationService` behind `INotificationService` without touching the endpoints
that fire events. `dispatched_at` is what distinguishes "we decided to notify" from
"we actually pushed".

A notification failure must never roll back the write that triggered it — delivery is
not fatal (§7), and the user's refresh button is the fallback.

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
