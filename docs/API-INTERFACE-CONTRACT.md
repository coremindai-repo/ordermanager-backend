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
- Notifications: push-only (mobile push via FCM/APNs through Azure
  Notification Hubs). No email, no SMS.
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
{ "platform": "ios | android", "pushToken": "string" }
```
Called on login and whenever the OS issues a new push token.

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

## 6. Raw materials & outsourcing/import (fixed sub-processes — not templatized)

### GET /api/raw-material-requests
### POST /api/raw-material-requests
### POST /api/raw-material-requests/{id}/status
Statuses: `requested → sent_to_supplier → order_placed → order_accepted → received`.
Supplier contact (WhatsApp) is a manual step outside the app; the app only
records the resulting status.

### GET /api/outsourcing-requests
### POST /api/outsourcing-requests
### POST /api/outsourcing-requests/{id}/status
Same manual-step pattern; statuses: `placed → accepted → received_semi_finished
| received_finished`.

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

## 12. Error shape (all endpoints)

```json
{ "error": { "code": "string", "message": "string" } }
```
`400` validation, `401` auth, `403` role, `404` not found, `409` illegal
status transition, `500` server error. Mobile app has one shared error
handler that maps these to toasts/banners.

## 13. Change process

Either team can propose a change to this contract, but it lands here first,
gets agreed, then both `CLAUDE.md` build sessions pick it up on their next
epic. Do not let backend and mobile drift on endpoint shapes independently.
