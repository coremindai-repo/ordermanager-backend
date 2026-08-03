# Orientation — start here

For a developer new to this repo (or returning after a break). Covers how a request
flows end to end, what is real versus stubbed, and the design decisions that look like
mistakes without context.

Companion docs:
- **CLAUDE.md** — the authoritative build rules and process design. Read §5 (workflow
  engine) before touching anything status-related.
- **docs/API-INTERFACE-CONTRACT.md** — the contract shared with the mobile repo. Both
  copies must stay identical.
- **docs/infrastructure.md** — what exists in Azure, configuration, and the go-live
  checklist.

---

## 1. How a request flows

Take `POST /api/orders/{orderId}/transition` — it touches nearly every layer.

```
HTTP request
  │
  ├─ Program.cs                          DI registration; middleware pipeline
  │
  ├─ Middleware/ExceptionHandlingMiddleware.cs
  │      Catches AppException and shapes ALL errors as
  │      { "error": { "code", "message" } } per contract §12.
  │      Endpoints therefore just throw; they never build error responses.
  │
  ├─ Functions/OrderTransition.cs        the endpoint
  │   │
  │   ├─ Lib/AuthHelper.cs               RequireCaller() → Caller(UserId, Roles)
  │   │      Validates the Bearer JWT (Lib/JwtService.cs, HS256, 12h).
  │   │      Roles come from the token, so no DB round-trip per request.
  │   │
  │   ├─ Lib/Workflow/TemplateProvider.cs
  │   │      Loads the client's active process_template and caches it for the
  │   │      LIFE OF THE WORKER INSTANCE. See §4 — this is deliberate.
  │   │
  │   ├─ Lib/Workflow/TransitionValidator.cs
  │   │      THE HEART OF THE SYSTEM. Pure: no SQL, no HTTP, no framework types.
  │   │      Decides allow/deny from the template alone — edges, roles, order
  │   │      type, method. Returns the matched rule so the caller can apply
  │   │      gates it cannot check itself.
  │   │
  │   ├─ gates the validator cannot evaluate (they need the database):
  │   │      Lib/Workflow/LineItemCompletion.cs   every line item finished?
  │   │      destination store set?               (inline in the endpoint)
  │   │
  │   ├─ Lib/Workflow/TransitionOutcomeMapper.cs
  │   │      Denial → HTTP status + code. One place, so 400/403/409 cannot drift.
  │   │
  │   ├─ SQL write, in a transaction:
  │   │      UPDATE orders ... WHERE current_status = <the status we validated>
  │   │        ↑ optimistic concurrency: two callers cannot both win
  │   │      INSERT INTO order_status_history ...   append-only, never updated
  │   │
  │   └─ Lib/Notifications/NotificationService.cs   (only if the matched rule
  │          carries notifyEvent)
  │            → resolves recipients from notification_recipients
  │            → writes notifications_log
  │            → Lib/Notifications/PushDispatcher.cs
  │                 → Lib/Notifications/ExpoPushClient.cs → exp.host
  │          Failures here are caught and logged; they NEVER roll back the write.
  │
  └─ 200 + JSON
```

**The shape to internalise:** decisions are pure and testable
(`TransitionValidator`, `LineItemCompletion`, `AccessScope`, `*StatusFlow`), and
everything impure — SQL, HTTP, Blob, Expo — sits at the edges. When adding behaviour,
put the *rule* in a pure class with tests and let the endpoint orchestrate.

## 2. Where things live

| Folder | What |
|---|---|
| `Functions/` | One HTTP endpoint per file. Thin: auth, validate input, call into `Lib/`, write, respond. |
| `Lib/Workflow/` | The status engine — templates, validation, gates. Start here. |
| `Lib/Notifications/` | Recipient routing, Expo push, dead-token pruning. |
| `Lib/Soho/` | The external sales-order integration. **Currently stubbed.** |
| `Lib/Photos/`, `Lib/Orders/`, `Lib/Inventory/`, `Lib/RawMaterials/`, `Lib/Outsourcing/` | Per-area helpers, mostly pure. |
| `Middleware/` | Error shaping. |
| `sql/` | Numbered migrations, applied in order. Templates are versioned *data*, so process changes appear here as new `*_template_v*.sql` files rather than code changes. |
| `tests/` | xUnit. Pure-logic focused; no database or network needed. |

**Reading order for a newcomer:** `Lib/Workflow/WorkflowTemplate.cs` →
`TransitionValidator.cs` → `Functions/OrderTransition.cs` → the latest
`sql/*_process_template_v*.sql`. That is the whole system in miniature.

## 3. Built vs stubbed vs deferred

### Fully built and tested
Auth (login, JWT, device registration) · the workflow engine and both templates ·
order capture · factory production flow with photo attachments · store manager flow
(invoicing handoff, raw materials) · outsourcing/import · push notifications via Expo ·
read APIs (orders, history, notifications, inventory, dashboard) · role gating on every
transition.

### Stubbed — works, but not real
- **SOHO** (`Lib/Soho/`). No real API from the client yet. `StubSohoClient` issues
  deliberately implausible `STUB…` references so stubbed orders are obvious
  (`CUS-STUB471203`). Active only when `SOHO_MODE=stub`; the default
  (`UnconfiguredSohoClient`) rejects customer orders with `503` rather than inventing
  references. **Go-live blocker.**

### Empty by design — not missing
- **`suppliers`** — the client's real list has not been shared. Inventing names would
  put fake business relationships in the database looking exactly like real ones.
- **No `inventory` table at all** — inventory is derived from stock orders. See §4.

### Not built — all confirmed decisions, not oversights

Each of these was raised and settled. They are recorded so nobody re-opens them as
"missing", and so a later phase knows what was deliberately left.

- **User management and password reset.** No endpoints, by decision. The pilot's user
  group is small and known, so accounts are provisioned by hand — see the runbook in
  `docs/infrastructure.md`. A user-management portal is planned as future work.
- **Expo delivery receipts.** We read Expo's immediate *ticket* response and act on it.
  Expo also exposes a delayed *receipts* endpoint reporting failures discovered after
  acceptance; we do not poll it. **Accepted for the pilot** — the consequence is that a
  push Expo accepted but FCM/APNs later rejected shows as delivered. Worth revisiting if
  delivery reliability becomes a complaint.
- **Notification read/unread state.** `notifications_log` records what was sent; there is
  no per-user read marker. A "recently sent" list is sufficient for the pilot.
- **Order cancellation / order-type conversion.** Out of scope this phase; `order_type`
  is fixed for an order's lifetime, which the template branching assumes.

  ⚠ **Two consequences of claiming that have no answer yet**, both waiting on
  cancellation being scoped. Neither breaks anything today; both would surface as
  puzzling behaviour:

  - **No release path.** An item claimed into an order that then stalls sits in
    `pending_sale` indefinitely — absent from inventory, unsellable, with nothing to
    return it to `available`. "The sale fell through" currently has no representation.
  - **Emptied originating orders.** Claiming reassigns `order_id`, so a stock order
    whose items are all claimed ends up with none. It is not stuck — it is already past
    the completeness gate — but it *would* be if it ever took the
    `KEEP_IN_FACTORY → IN_PRODUCTION` revert, because `AllComplete([])` returns `false`
    by design. Whether such an order should auto-close is undecided.
- **Speech-to-text.** Confirmed out of scope for the pilot. If it lands later it is an
  Azure Speech call from the mobile app, never a backend proxy (CLAUDE.md §8).
- **ZOHO invoicing** — never in scope; invoicing is manual outside the app.
- **API Management, staging slot, Cosmos DB** — all deliberately deferred.

## 4. Read this before you touch…

Each of these looks like a bug or an oversight without the reasoning.

### …a workflow template
- **Templates are cached for the life of the worker instance.** Editing a template row
  does nothing until the Function App is redeployed. This is intended, not a missing
  invalidation feature. Worse: editing *without* redeploying makes instances disagree,
  because Flex Consumption scales them in and out. Always pair a template edit with a
  redeploy.
- **Removing a status strands every order sitting in it** — the validator reports
  `UnknownCurrentStatus` and the order cannot move in any direction. Migrations that
  remove a status must migrate affected orders in the same script; `sql/009` and
  `sql/016` show the `THROW` guard pattern.
- **Every transition must carry `allowedRoles`.** An edge without it is open to
  everyone. `RoleGatingTests.NoTransitionIsLeftUngated` fails the build if one is added
  without roles.
- **Tests embed copies of template JSON.** They will not fail when a seed changes — they
  will keep certifying the retired version. Grep the tests for any status or flag you
  change. This has already bitten twice.

### …role assignments
- **The supplier edges on the production template are `company_manager`, not
  `factory_supervisor`, deliberately.** Those line-item moves are driven by the
  outsourcing endpoints, which validate with the *requester's* roles, and
  `AdvanceLineItemsAsync` skips-and-logs on refusal rather than failing. Gating them to
  factory_supervisor would leave every linked item behind while the request reported
  success. Change those edges and the endpoint guard together, never separately.
- **`factory_supervisor` owns two transitions that look store-side** —
  `KEEP_IN_FACTORY → SENT_TO_WAREHOUSE` and
  `READY_TO_DELIVER → KEEP_IN_FACTORY`/`SENT_TO_WAREHOUSE`. Both are goods movements at
  the factory end; the second is the dispatch decision after invoicing clears, not a
  revert of invoicing. Confirmed with the client.
- **Raising a raw material request is ungated on purpose.** Contract §3 says a
  factory_supervisor raises them; they just cannot progress one through supplier contact.

### …claimed stock
- **After a claim, `order_id` means "who delivers this", not "who made this".** Use
  `COALESCE(originating_order_id, order_id)` for anything reporting on production. A
  query written from habit will silently attribute production to the claiming order.
- `availability_status` (`available → pending_sale → sold`) is a **sale** lifecycle,
  entirely separate from production status, and NULL for items made to order. Both
  transitions are side effects — claiming, and the claiming order completing — never
  endpoints, so a sale cannot be recorded without a real order behind it.
- `GET /api/inventory` deliberately omits `originating_order_id`: inventory lists only
  unclaimed items, so it would be null on every row. It appears on order detail instead.

### …inventory
- **There is no inventory table and there should not be one.** Inventory is a query over
  finished/semi-finished line items of undelivered *stock* orders. Storing it would need
  three concepts the data model lacks: committed-vs-spare (else a customer's finished
  chair reads as available stock), a decrement on dispatch (else the count only grows),
  and product identity (else "Teak Chair" and "Teak chair" are two products). See
  CLAUDE.md §8a.

### …statuses
- **`SEMI_FINISHED` is outsource-only**, and this is settled rather than an oversight.
  An outsourcing supplier may do part of the job; an import always arrives complete; and
  a part-built factory item is work in progress on a production step, not a returned
  state. **A factory-produced item is only ever inventory-eligible once `FINISHED`.**
  Enforced in both the template and `OutsourcingStatusFlow`.

  Semi-finished *stock* therefore has exactly two legitimate origins, both expected:
  a cancelled customer order leaving a part-built outsourced item stranded, and the
  company manager deliberately outsourcing an item for stock that comes back partly
  done.

- **Finished vs semi-finished is decided at RECEIPT, not when the outsourcing request is
  placed.** The request records what the supplier actually delivered
  (`received_finished` / `received_semi_finished`), so nothing needs to be predicted
  upfront. Same either way, whether the outsourcing serves a stock or a customer order.
- **Raw material and outsourcing chains are hard-coded, not templatized.** Contract §6
  calls them fixed sub-processes. Their unit tests are the only thing protecting them.

### …logging
- **Worker `ILogger` output does not reach the console or the Azure log stream.**
  `host.json` sets `telemetryMode: OpenTelemetry`, which routes it to Azure Monitor only.
  Anything logged before `app.Run()` is dropped entirely. Lines that must be *noticed*
  (startup banners, silent-failure warnings) write to stdout as well — see CLAUDE.md §2.
  If a warning you added seems not to fire, check this before hunting for a logic bug.

### …photos
- **Only blob paths are stored, never URLs or SAS tokens.** Read URLs are minted per
  response and expire in ~15 minutes. Upload uses a write-only, single-blob SAS valid
  ~10 minutes; bytes go device → Blob, never through a Function.
- Because the backend never sees the bytes, everything checkable is validated at
  *confirmation* time — path ownership, existence, size, content type.

### …push notifications
- **A 200 from Expo does not mean delivered.** Per-token failures arrive inside the
  response body. Read the body, not the status code.
- Dead tokens (`DeviceNotRegistered`) are pruned on first report. Credential errors
  (`MismatchSenderId`, `InvalidCredentials`) are *never* pruned — the tokens are fine and
  the credentials are not; pruning would delete healthy registrations fleet-wide.
- **No Firebase or Apple credentials live in this repo.** Expo brokers delivery; they are
  configured in the mobile repo via EAS.

### …free-form JSON columns
- `materials`, `billTo`, `shipTo`, and raw-material/outsourcing `items` are stored as
  whatever JSON the app sends. **Agreed to stay this way until the real screen field
  lists are available** — not an oversight, and not something to tighten unilaterally.
  When those lists arrive, pin the shapes down in the contract first, then tighten the
  schema to match. Until then the looseness is the deliberate trade for not inventing a
  layout that would need migrating.

### …the `tab` query param
- `GET /api/orders?tab=` is treated as an alias for `status` when `status` is absent.
  This is a **placeholder pending the mobile team's actual tab-to-status mapping**, which
  gets built against the wireframes in mobile Epic 1. Expect to revisit it then; the
  backend side is a one-line change once the mapping is known.

### …the inventory status filter
- `status=finished` and `status=semi_finished` map to the literal `FINISHED` and
  `SEMI_FINISHED` line-item statuses. Items mid-production (`CARPENTRY`, `POLISHING`, …)
  deliberately **do not appear** — they are work in progress, not stock. Confirmed
  correct; do not widen it to "anything not finished" without asking.
