# API Interface Contract — Backend ↔ Mobile

This is the shared contract between the `order-management-backend` and
`order-management-mobile` repositories. Both Claude Code sessions should treat
this file as authoritative and identical — copy it verbatim into both repos
at `/docs/API-INTERFACE-CONTRACT.md`. If either side needs a change, update
this file first and re-sync both repos before writing code against it.

## 1. Scope of this pilot

- Single client/tenant running live; multi-client config exists in the data
  model but only one active `process_template` is in use.
- Auth: username/password against an internal Users table. No IAM/SSO.
- Data refresh: pull-only. Every screen has a manual refresh action that
  re-calls the relevant GET endpoint. No WebSocket/SignalR/long-poll.
- Notifications: push-only, delivered through the **Expo Push API**. The
  backend posts to `https://exp.host/--/api/v2/push/send` using the device's
  stored Expo push token; Expo brokers delivery on to FCM and APNs. No email,
  no SMS.
  - The backend holds **no Firebase or Apple credentials**. Those are
    configured on the mobile side via EAS (`eas credentials`), so a delivery
    failure affecting a whole platform is a mobile-repo concern, not a
    backend one.
- No API Management gateway. Functions are called directly over HTTPS.

## 2. Auth

### POST /api/auth/login
Request:
```json
{ "username": "string", "password": "string" }
```
Response `200`:
```json
{
  "token": "jwt-string",
  "expiresAt": "2026-08-01T10:00:00Z",
  "user": {
    "userId": "guid",
    "firstName": "string",
    "lastName": "string",
    "mobileNo": "string",
    "roles": ["salesperson", "factory_supervisor", "store_manager", "company_manager"]
  }
}
```
- Token is a JWT, HS256, embedding `userId` and `roles`. Lifetime: 12 hours
  (pilot — user re-logs in daily; no refresh-token flow needed yet).
- Every subsequent call sends `Authorization: Bearer <token>`.
- `401` on bad credentials or expired token → mobile app routes to login screen.

### POST /api/auth/register-device
Registers a push token against a user, for targeted notifications.
```json
{ "platform": "ios | android", "pushToken": "ExponentPushToken[...]" }
```
Called on login and whenever Expo issues a new push token.

- **`pushToken` must be an Expo push token** — `ExponentPushToken[...]`, or the
  legacy `ExpoPushToken[...]` prefix Expo also issues. Obtain it from
  `Notifications.getExpoPushTokenAsync()`, not from the OS directly.
- **A raw FCM or APNs token is rejected with `400`.** Sending one means the app
  registered against the platform rather than through Expo, and the backend
  cannot deliver to it — Expo would report the failure per-token inside an
  otherwise-successful response, so it is refused at registration where the
  error is visible.
- One token is stored per user per platform; re-registering replaces it.

## 3. Roles → screen access (used to gate menus/tabs on mobile, enforced again server-side on every call)

| Role | Can see |
|---|---|
| `salesperson` | Own orders, order creation, inventory search, own notifications |
| `factory_supervisor` | All items in production stages, production step config, raw-material requests they raise |
| `store_manager` | All orders, invoicing, item logistics, raw-material procurement |
| `company_manager` | Everything store_manager sees, plus outsourcing/import screens |

Server is the source of truth for authorization — the mobile app hiding a
tab is a UX convenience, not a security control. Every endpoint below
re-checks role.

## 4. Orders

### GET /api/orders
Query params: `type` (`stock`|`customer`), `status`, `tab`, `mine` (bool).
Returns a list of order summaries for the dashboard tabs (Ready to Invoice,
Ready to Deliver, In Production, etc.). `mine=true` restricts to the caller's
own orders (default behavior for `salesperson` role unless they hold a
supervisory role too).

> **`tab` — UNCONFIRMED, needs mobile team input.** The backend currently
> treats `tab` as an alias for `status`, used only when `status` is not
> supplied. That is a guess based on the tab names in this section being
> status values. If the dashboard tabs actually map to something else (a
> group of statuses, or a status plus a role filter), say so and the mapping
> will be defined here properly before either side relies on it.

Response `200`:
```json
{
  "orders": [
    {
      "orderId": "guid",
      "orderNumber": "string",
      "orderType": "customer | stock",
      "currentStatus": "string",
      "storeName": "string|null",
      "salespersonName": "string",
      "lineItemCount": 0,
      "createdAt": "2026-08-02T10:02:58.8478931Z",
      "updatedAt": "2026-08-02T10:02:58.8478931Z"
    }
  ],
  "count": 1
}
```

### GET /api/orders/{orderId}
Full order detail: header, billing/shipping, line items with current status,
salesperson, showroom, timestamps. Response `200` — same shape returned by
`POST /api/orders`:
```json
{
  "orderId": "guid",
  "orderNumber": "string",
  "orderType": "customer | stock",
  "sohoOrderRef": "string|null",
  "currentStatus": "string",
  "createdAt": "2026-08-02T10:02:58.8478931Z",
  "updatedAt": "2026-08-02T10:02:58.8478931Z",
  "salesperson": { "userId": "guid", "firstName": "string", "lastName": "string" },
  "store": { "storeId": "guid", "name": "string", "location": "string|null" },
  "billTo": { },
  "shipTo": { },
  "lineItems": [
    {
      "lineItemId": "guid",
      "itemName": "string",
      "description": "string|null",
      "currentStatus": "string",
      "method": "factory | outsource | import | null",
      "currentStep": "string|null",
      "materials": [ { } ]
    }
  ]
}
```
`404` if the order does not exist.

### POST /api/orders
Creates a new order (customer or stock). See §7 for the submit sequence.

Request:
```json
{
  "orderType": "customer | stock",
  "storeId": "guid|null",
  "lineItems": [
    {
      "itemName": "string",
      "description": "string|null",
      "method": "factory | outsource | import | null",
      "materials": [ { } ]
    }
  ],
  "billTo": { },
  "shipTo": { }
}
```
- At least one line item is required; every line item requires `itemName`.
- `storeId`, if supplied, must be an active store (`400` otherwise).
- Response `201` with the full order detail shape shown under
  `GET /api/orders/{orderId}` above.
- `503` with code `SOHO_UNAVAILABLE` if `orderType` is `customer` and SOHO
  cannot be reached — no order is created. Stock orders never call SOHO.

> **`materials`, `billTo` and `shipTo` are intentionally free-form JSON
> objects for now.** The backend stores whatever object the app sends,
> unchanged, because the field lists for the "Add Material", billing and
> shipping screens have not been shared yet. Once those are confirmed, these
> shapes should be pinned down here and the backend will tighten its schema
> to match — so treat the current freedom as temporary, not as a licence to
> send arbitrary structures long-term.

### Order numbers
| Order type | Format | Example |
|---|---|---|
| Customer | `CUS-` + SOHO sales order number | `CUS-4471` |
| Stock | `STK-{yyMM}-{sequence}` | `STK-2608-0042` |

While the SOHO integration is stubbed (pre-go-live), customer order numbers
appear as `CUS-STUB471203` — the `STUB` marker means the reference is a
placeholder and does not exist in SOHO.

### POST /api/orders/{orderId}/transition
Generic status-transition endpoint — the workflow engine. Body:
```json
{ "targetStatus": "string", "notes": "string", "photoUrls": ["string"] }
```
Server validates the transition against the client's active
`process_template` (illegal transitions return `409`). Every successful
transition writes an immutable row to `order_status_history`.

Some transitions are additionally gated on the whole order being finished. Moving
out of production is one: an order's line items ship together, so the order cannot
leave the factory until **every** line item on it is complete — not merely every
step within one line item. Attempting it early returns `409` with code
`LINE_ITEMS_INCOMPLETE` (distinct from `ILLEGAL_TRANSITION`: the move is legal in
principle, the order just isn't ready, so the app should prompt to finish the
remaining items rather than say "not allowed"). The message names the statuses still
blocking.

Dispatching towards a store is gated too: `→ IN_TRANSIT` returns `409`
`DESTINATION_STORE_REQUIRED` unless the order already has a destination set via
`POST /api/orders/{orderId}/destination-store`. Also actionable rather than
forbidden — the app should open the store picker.

### Order statuses

The current process template (v4). **The flow branches on `orderType`** — invoicing
happens immediately after production and applies to customer orders only:

```
                        ┌── customer ──→ READY_TO_INVOICE → READY_TO_DELIVER ──┐
NEW → IN_PRODUCTION ────┤   [all items complete]                               │
                        └── stock ─────────────────────────────────────────────┤
                            [all items complete]                               │
                                                                               ↓
                                            ┌──────────────────────────────────┴──┐
                                            ↓                                     ↓
                                     KEEP_IN_FACTORY ──────────→ SENT_TO_WAREHOUSE
                                            └──[destination store set]──┬─────────┘
                                                                        ↓
                          IN_TRANSIT → SENT_TO_STORE → RECEIVED_IN_STORE
                                                              ↓
                                              OUT_FOR_DELIVERY → DELIVERED
```

**A stock order can never reach `READY_TO_INVOICE` or `READY_TO_DELIVER`**, and a
customer order cannot skip them. Attempting either returns `409 ILLEGAL_TRANSITION`
with a message naming the order type. Everything from `KEEP_IN_FACTORY` onward is
shared by both types.

| Status | Meaning |
|---|---|
| `NEW` | Captured, not yet in production |
| `IN_PRODUCTION` | Being made |
| `READY_TO_INVOICE` | **Customer orders only** — awaiting invoicing (manual, outside the app) |
| `READY_TO_DELIVER` | **Customer orders only** — invoiced, cleared to dispatch |
| `KEEP_IN_FACTORY` | Complete, held at the factory |
| `SENT_TO_WAREHOUSE` | Moved to the warehouse |
| `IN_TRANSIT` | Moving to the destination store |
| `SENT_TO_STORE` | Despatched to the store |
| `RECEIVED_IN_STORE` | Confirmed arrived at the store |
| `OUT_FOR_DELIVERY` | Out for delivery to the customer |
| `DELIVERED` | Complete — the only dead end |

**Statuses never name a store.** The destination is carried by the order's `store`
field, so adding a third store is a data change and does not multiply the status list.

Reverts permitted: `KEEP_IN_FACTORY`/`SENT_TO_WAREHOUSE → IN_PRODUCTION` (rework),
`IN_TRANSIT → SENT_TO_WAREHOUSE` (shipment recalled), `SENT_TO_STORE → IN_TRANSIT`
(wrong store), `RECEIVED_IN_STORE → SENT_TO_STORE` (marked received in error),
`OUT_FOR_DELIVERY → RECEIVED_IN_STORE` (failed delivery). Any other backward move
returns `409`. Reverts apply to both order types.

Note that reverting a **customer** order to `IN_PRODUCTION` means it re-traverses
invoicing on the way back out — reworked goods are re-invoiced.

### POST /api/order-line-items/{lineItemId}/transition
Same shape as above, scoped to a single line item, validated against
whichever sub-flow the item is currently in (factory production steps,
outsourcing/import, post-production).

## 5. Factory production

### GET /api/production-steps-template
Returns the client's configured list of production stages (e.g. Carpentry,
Polishing, Upholstery, Finished) — drives the "This item will require"
checklist on Screen 2.

### POST /api/order-line-items/{lineItemId}/production-plan
Body: `{ "method": "factory | outsource | import", "steps": ["carpentry","polishing"] }`
Sets the chosen path and step list for one item.

### POST /api/order-line-items/{lineItemId}/production-steps/{stepId}/update
Body: `{ "status": "started | complete", "assignedNames": ["string"], "photoUrls": ["string"] }`

- `photoUrls` carries **blob paths** returned by the photo upload flow below — not
  full URLs, and never the SAS URL. Paths not belonging to this step are rejected
  with `400`.
- Step status moves forward only: `pending → started → complete`, or
  `pending → complete` for a step done in one go. Re-sending the current status
  returns `409` (so a double tap cannot reset timestamps).
- Photos accumulate: those attached when starting a step survive its completion.
- Response `200`:
```json
{
  "stepId": "guid",
  "lineItemId": "guid",
  "status": "started | complete",
  "photos": [ { "blobPath": "string", "url": "https://…?<read SAS>" } ],
  "allStepsComplete": false,
  "updatedAt": "2026-08-02T10:36:40.4538577Z"
}
```

### Photo upload (production step attachments)

Photos go **from the device straight to Azure Blob storage**, not through the API —
image bytes never pass through a Function. Three steps:

**1. Ask for an upload URL**

`POST /api/order-line-items/{lineItemId}/production-steps/{stepId}/photo-upload-url`
```json
{ "fileExtension": "jpg" }
```
Allowed extensions: `jpg`, `jpeg`, `png`, `heic`, `webp` (anything else → `400`).

Response `200`:
```json
{
  "uploadUrl": "https://<account>.blob.core.windows.net/production-photos/…?<write SAS>",
  "blobPath": "{orderId}/{lineItemId}/{stepId}/{guid}.jpg",
  "expiresAt": "2026-08-02T10:47:38.4343303Z",
  "requiredHeaders": { "x-ms-blob-type": "BlockBlob" }
}
```

**2. PUT the image bytes to `uploadUrl`**

> ⚠ **The `x-ms-blob-type: BlockBlob` header is required.** Azure rejects the PUT
> without it. No Azure SDK is needed — a plain HTTPS PUT with the raw bytes as the
> body is enough. Setting `Content-Type` to the real image type is recommended; the
> backend checks it on confirmation.

The upload SAS is write-only, scoped to that single blob, and valid for about
**10 minutes** — request a fresh one rather than storing it.

**3. Confirm by sending `blobPath` in `photoUrls`** on the step update call above.

The backend never sees the bytes, so it validates at this point that the blob belongs
to the step, actually exists, is within the size limit (15MB), and looks like an
image. Confirming a path that was never uploaded returns `400`.

**Reading photos back.** Responses return a fresh read URL alongside the stored path.
Those URLs are short-lived (about **15 minutes**) — display them, don't cache or share
them. Re-fetch the order or step to get fresh URLs.

## 6. Raw materials & outsourcing/import (fixed sub-processes — not templatized)

Statuses: `requested → sent_to_supplier → order_placed → order_accepted → received`.
Supplier contact (WhatsApp) is a manual step outside the app; the app only
records the resulting status.

The chain moves **forward one step at a time**. Skipping ahead, going back, and
re-sending the current status all return `409 ILLEGAL_TRANSITION`; the message names
the only status that is actually reachable next. Responses carry `nextStatus` (null at
the end of the chain) so the app can render the next action without hard-coding the
sequence.

### GET /api/raw-material-requests
Query param: `status` (any of the five above).

Callers holding `store_manager` or `company_manager` see all requests; anyone else
sees only the requests they raised themselves (contract §3 — factory supervisors see
"raw-material requests they raise").

Response `200`:
```json
{
  "requests": [
    {
      "requestId": "guid",
      "items": [ { } ],
      "status": "requested",
      "nextStatus": "sent_to_supplier",
      "supplier": { } ,
      "notes": "string|null",
      "requestedBy": { "userId": "guid", "name": "string" },
      "createdAt": "2026-08-02T11:20:00.0000000Z",
      "updatedAt": "2026-08-02T11:20:00.0000000Z"
    }
  ],
  "count": 1
}
```

### POST /api/raw-material-requests
```json
{ "items": [ { } ], "supplier": { }, "notes": "string|null" }
```
`items` is required and must be a non-empty JSON array or object. Like `materials`
elsewhere, `items` and `supplier` are free-form for now — the field lists come from
screens that have not been shared yet.

Response `201`: `{ "requestId": "guid", "status": "requested", "nextStatus": "sent_to_supplier" }`

### POST /api/raw-material-requests/{id}/status
```json
{ "status": "sent_to_supplier", "supplier": { }, "notes": "string|null" }
```
`supplier` is merged when supplied and left untouched when omitted, so supplier
details can be filled in at whichever step they become known.

Response `200`:
```json
{
  "requestId": "guid",
  "previousStatus": "requested",
  "status": "sent_to_supplier",
  "nextStatus": "order_placed",
  "updatedAt": "2026-08-02T11:21:00.0000000Z"
}
```

Same manual-step pattern; statuses: `placed → accepted → received_semi_finished
| received_finished`.

The chain **branches at the end**, and the branch decides what happens to the line
items: finished goods are done, semi-finished goods still need factory work. Both
receipt states are terminal — neither converts into the other, and a semi-finished
item finishes by going through the factory steps, not by re-reporting the receipt.
Skipping acceptance, going backwards and restating the current status all return
`409`. Responses carry `nextStatuses` (an array, since `accepted` has two).

### GET /api/suppliers
The predefined picker for outsourcing/import. Optional `method` filter
(`outsource`|`import`) — not every supplier serves both.

```json
{
  "suppliers": [
    { "supplierId": "guid", "name": "string", "contact": "string|null",
      "supportsOutsource": true, "supportsImport": false }
  ],
  "count": 1
}
```

### GET /api/outsourcing-requests
Query params: `status`, `method`.

### POST /api/outsourcing-requests
```json
{
  "method": "outsource | import",
  "supplierId": "guid|null",
  "lineItemIds": ["guid"],
  "items": [ { } ],
  "notes": "string|null"
}
```
- At least one `lineItemId` is required, and **every named line item must already be
  set to that method** — an item's route is chosen on its production plan, so a
  request cannot silently reroute an item planned for the factory (`400` otherwise).
- `supplierId`, if given, must be active and serve that method (`400` otherwise).
- Response `201` with `requestId`, `status: "placed"`, `nextStatuses`, and
  `lineItemsAdvanced` — placing the request is what sends the goods out, so the linked
  items move to `WITH_SUPPLIER` immediately.

### POST /api/outsourcing-requests/{id}/status
```json
{ "status": "accepted", "supplierId": "guid|null", "notes": "string|null" }
```
Response `200` adds `lineItemsAdvanced` and `requiresProductionPlan`. On
`received_semi_finished` the latter is `true` — those items need a production plan
before they can continue.

### How line items move through outsourcing

The request status drives the linked line items, so the same physical event is not
recorded twice:

| Request reaches | Line items move to | Then |
|---|---|---|
| `placed` | `WITH_SUPPLIER` | Waiting on the supplier |
| `received_finished` | `FINISHED` | Done — counts toward the order's completeness |
| `received_semi_finished` | `SEMI_FINISHED` | Needs a production plan, then the normal factory steps |

**Semi-finished items re-enter the same step checklist factory items use** — set a
production plan via `POST /api/order-line-items/{id}/production-plan` and drive the
steps exactly as for a factory item. There is no separate outsourcing step mechanism.

`FINISHED` remains the only terminal production status, so an outsourced item counts
as complete on exactly the same condition as a factory one: the order cannot leave
production while any item sits at `WITH_SUPPLIER` or `SEMI_FINISHED`.

An outsourced or imported item **cannot skip its supplier stage** — `PENDING` leads
only to `WITH_SUPPLIER` for those methods, and only to factory steps for `factory`.

## 7. Order submission sequence (server-side, triggered by POST /api/orders)

1. If `orderType == customer`: call SOHO API to create a draft Sales Order →
   receive SOHO order number → use it as the app's `orderNumber`.
2. If `orderType == stock`: generate an internal stock order number.
3. Persist order + line items + billing/shipping + materials, all fields
   timestamped and tagged with the submitting user.
4. Set initial status per the client's process template (`NEW`).
5. Return the created order to the mobile app.

## 8. Stores (reference data, not templatized)

### GET /api/stores
Returns active stores (currently Kochi, Bangalore) for post-production
routing pickers. Adding a store is a data change, not a deploy.

Response `200`:
```json
{ "stores": [ { "storeId": "guid", "name": "string", "location": "string|null" } ] }
```

### POST /api/orders/{orderId}/destination-store
Post-production routing — the action behind the store picker above.
```json
{ "storeId": "guid" }
```
Response `200`:
```json
{
  "orderId": "guid",
  "store": { "storeId": "guid", "name": "string" },
  "updatedAt": "2026-08-02T10:40:42.2274777Z"
}
```

**Routing is per order, not per line item.** An order's line items ship together: the
order only leaves the factory once every line item on it is complete, and the whole
order then moves as one unit to a single store. `400` if the store does not exist or
is inactive.

## 9. Inventory search

### GET /api/inventory?query=&status=finished|semi_finished
Available to all authenticated users.

## 10. Notifications & history

### GET /api/notifications
Server-recorded log of pushes sent to the caller (for the in-app
notification list, independent of whether the OS push was seen).

### GET /api/order-history
Filtered order/status history, scoped by role per §3.

## 11. Push notification payload (server → device, out of band of REST)

```json
{
  "type": "order_status_changed | invoice_ready | raw_material_received | item_assigned",
  "orderId": "guid",
  "lineItemId": "guid|null",
  "title": "string",
  "body": "string"
}
```
Mobile app on receipt: shows OS notification; if the app is foregrounded on
the relevant screen, surfaces an "Updates available — refresh" banner rather
than auto-refreshing (no silent background refresh in this scope).

> **Where these fields actually arrive.** The payload above is the logical
> contract, not the wire format. The Expo Push API takes
> `{ to, title, body, data }`, so `title` and `body` map to Expo's own fields
> and the remaining custom fields (`type`, `orderId`, `lineItemId`) are nested
> under `data`. On the device they are read from
> `notification.request.content.data`, **not** from the top level:
>
> ```js
> const { type, orderId, lineItemId } = notification.request.content.data;
> ```
>
> `title` and `body` remain where Expo puts them
> (`notification.request.content.title` / `.body`).

## 12. Error shape (all endpoints)

```json
{ "error": { "code": "string", "message": "string" } }
```
`400` validation, `401` auth, `403` role, `404` not found, `409` illegal
status transition, `500` server error. Mobile app has one shared error
handler that maps these to toasts/banners.

Codes seen in practice:

| Code | Status | Meaning |
|---|---|---|
| `VALIDATION_ERROR` | 400 | Malformed or missing input |
| `UNAUTHORIZED` | 401 | Missing, invalid or expired token |
| `FORBIDDEN` | 403 | Caller's roles do not permit the action |
| `NOT_FOUND` | 404 | No such order / line item / step |
| `ILLEGAL_TRANSITION` | 409 | The move is not permitted by the template, or the record changed concurrently |
| `LINE_ITEMS_INCOMPLETE` | 409 | Transition is gated on every line item being complete, and some are not |
| `DESTINATION_STORE_REQUIRED` | 409 | Dispatching towards a store with no destination set |
| `PLAN_LOCKED` | 409 | The production plan cannot change once work has started |
| `SOHO_UNAVAILABLE` | 503 | SOHO could not be reached; no customer order was created |
| `INTERNAL_ERROR` | 500 | Unexpected server error |

## 13. Change process

Either team can propose a change to this contract, but it lands here first,
gets agreed, then both `CLAUDE.md` build sessions pick it up on their next
epic. Do not let backend and mobile drift on endpoint shapes independently.
