# Infrastructure

This file records every Azure resource currently provisioned for this
project. Keep it in sync as later epics add resources — this should always
reflect what's actually in Azure, not what was originally planned.

Subscription: `Azure subscription 1` (`761807b1-4e09-4429-ae58-4419e856128b`)
Tenant: `coremind.co.in` (`bee4b7aa-ef0b-47a3-8b1b-986b63440ad1`)

## Resource group

| Name | Region |
|---|---|
| `rg-ordermanager-nilambur` | Central India |

All resources below live in this resource group unless noted.

## Resources (Epic 1)

| Resource | Name | Region | Tier / SKU | Notes |
|---|---|---|---|---|
| SQL logical server | `sql-ordermanager-nilambur` | Central India | — | FQDN: `sql-ordermanager-nilambur.database.windows.net`. Admin login: `sqladmin`. Password stored only in the Function App's `SQL_CONNECTION_STRING` app setting — not written down anywhere else. |
| SQL database | `sqldb-ordermanager` | Central India | General Purpose, Serverless, Gen5, 1 vCore ceiling, 0.5 vCore floor, auto-pause after 60 min idle | Pay-per-second compute while active; storage billed separately (~32GB max size configured). |
| SQL firewall rule | `AllowAzureServices` | — | — | `0.0.0.0`–`0.0.0.0` (the special "allow Azure services" rule). |
| SQL firewall rule | `AllowLocalDev` | — | — | Single IP, added for local development from the machine that provisioned this epic. **This will need updating/removing as dev machines change** — it is not a durable rule. |
| Storage account | `stordermanagernilambur` | Central India | Standard_LRS, StorageV2 | Required by the Function App runtime (triggers/bindings state), not used for blob/file storage yet. |
| Function App | `func-ordermanager-nilambur` | Central India | **Flex Consumption**, runtime `dotnet-isolated`, version `10` | URL: `https://func-ordermanager-nilambur.azurewebsites.net`. Flex Consumption does not support publish-profile (Kudu basic-auth) deployment — see CI section below. |
| Application Insights | `appi-ordermanager-nilambur` | Central India | Workspace-based, pay-per-GB ingested | Required — the default .NET 10 isolated-worker Functions template wires OpenTelemetry to Azure Monitor at startup and fails to start without a connection string configured. |
| Storage account (photos) | `stordermgrphotosnilambur` | Central India | Standard_LRS, StorageV2, blob public access **disabled** | Production step photos. Separate from the Functions runtime account on purpose — see the per-client pattern below. Single private container `production-photos`; blobs laid out as `{orderId}/{lineItemId}/{stepId}/{guid}.{ext}`. |

## Per-client provisioning pattern

**Every client gets their own resource group containing their own complete stack** —
SQL server, database, Function App, storage accounts, Application Insights. Nothing is
shared across clients. A client's orders, database and photos are fully self-contained
in one resource group, so a client can be onboarded, audited, exported or deleted as a
unit, and a mis-scoped credential can never reach another client's data.

When onboarding a second client, repeat the whole pattern with their name in place of
`nilambur`:

| Resource | Pattern | This client |
|---|---|---|
| Resource group | `rg-ordermanager-{client}` | `rg-ordermanager-nilambur` |
| SQL server | `sql-ordermanager-{client}` | `sql-ordermanager-nilambur` |
| Function App | `func-ordermanager-{client}` | `func-ordermanager-nilambur` |
| Runtime storage | `stordermanager{client}` | `stordermanagernilambur` |
| Photo storage | `stordermgrphotos{client}` | `stordermgrphotosnilambur` |
| App Insights | `appi-ordermanager-{client}` | `appi-ordermanager-nilambur` |

⚠ **Storage account names are capped at 24 characters by Azure**, which is why the
photo account abbreviates "manager" to "mgr". `stordermgrphotosnilambur` is *exactly*
24 — meaning this pattern only fits client suffixes of **8 characters or fewer**.
A longer client name will need a different scheme (e.g. `stphotos{client}`, which
leaves 16 characters). Decide that when it first comes up and record it here, rather
than silently truncating a client's name.

### Function App settings (values not recorded here — see Azure Portal / `az functionapp config appsettings list`)

- `SQL_CONNECTION_STRING` — Microsoft.Data.SqlClient connection string to `sqldb-ordermanager`.
- `JWT_SECRET` — HS256 signing key, 12h token expiry (per API-INTERFACE-CONTRACT.md §2).
- `APPLICATIONINSIGHTS_CONNECTION_STRING` — points at `appi-ordermanager-nilambur`.
- `CLIENT_ID` — `c6c944a9-b531-4c21-a3fd-9a8d6df2b180`, the pilot client. Selects which
  row in `process_templates` / `production_step_templates` the workflow engine loads.
  Not a secret; it is the tenant key for the multi-client template design.
- `SOHO_MODE` — currently `stub`. **Must be removed before go-live** — see the SOHO
  section below.
- `PHOTO_STORAGE_CONNECTION_STRING` — connection string for `stordermgrphotosnilambur`.
  Contains the account key, which is what lets the app mint SAS tokens.
- `PHOTO_CONTAINER_NAME` — `production-photos`.

Local dev mirrors these in `local.settings.json` (gitignored, never committed).

### Operational note: workflow templates are cached until redeploy

The active templates are read from SQL once per worker instance and cached for the
life of that process. **Editing `process_templates` or `production_step_templates`
has no effect on the running app until it is redeployed** — intended behaviour, since
template changes go through client approval and a dev-initiated redeploy anyway.

Because the cache is per instance and Flex Consumption scales instances in and out, a
template edit *without* a redeploy causes divergence: existing instances keep the old
rules, newly-started ones pick up the new rules, and which one a request hits is
arbitrary. Always redeploy after changing a template row. See CLAUDE.md §5.

## CI/CD

GitHub Actions workflow: `.github/workflows/deploy.yml`, deploys on push to `main`.

Flex Consumption requires Microsoft Entra ID authentication for deployment
(no publish-profile support), so CI auth uses an OIDC federated credential
instead of a stored secret:

| Item | Value |
|---|---|
| App registration | `gha-ordermanager-backend-deploy` (app/client ID `b03888c3-6234-4bd6-85a1-d236689ee261`) |
| Role assignment | `Contributor`, scoped to `rg-ordermanager-nilambur` only (not subscription-wide) |
| Federated credential | `github-actions-deploy-main`, subject `repo:coremindai-repo@309092032/ordermanager-backend@1318263972:ref:refs/heads/main` |

The federated credential subject uses GitHub's immutable owner/repo IDs
(`309092032` / `1318263972`, verified against the live repo) rather than the
name-based `repo:coremindai-repo/ordermanager-backend:ref:...` form originally
configured — the name-based version is what initially failed CI with a
generic `az` auth-type error. ID-based subjects also survive an org or repo
rename, where a name-based subject would silently stop matching (or worse,
match whoever claims the old name).

GitHub Actions repo secrets required under Settings → Secrets and variables →
Actions (added manually — no GitHub write access from this session):

- `AZURE_CLIENT_ID` = `b03888c3-6234-4bd6-85a1-d236689ee261`
- `AZURE_TENANT_ID` = `bee4b7aa-ef0b-47a3-8b1b-986b63440ad1`
- `AZURE_SUBSCRIPTION_ID` = `761807b1-4e09-4429-ae58-4419e856128b`

None of these are secret in the sense of a password — OIDC means there is no
client secret to leak — but they're scoped to exactly one resource group via
the role assignment above. CI build-and-deploy confirmed green as of this
update.

## ⚠ SOHO integration is a stub, not a finished integration

The client has not provided their SOHO API yet. `SOHO_MODE=stub` is currently set on
`func-ordermanager-nilambur`, which means **customer orders created against the
deployed app carry invented sales order references**, not real ones. Stubbed orders
are visibly marked — their numbers look like `CUS-STUB471203` — so they can be found
and cleaned up later:

```sql
SELECT id, order_number FROM orders WHERE soho_order_ref LIKE 'STUB%';
```

It is set deliberately: there is no separate staging environment (see gaps below), so
this deployment is also the test environment, and without it customer orders cannot be
exercised at all. With `SOHO_MODE` unset, customer submissions fail with
`503 SOHO_UNAVAILABLE` and stock orders continue to work normally.

### How to check which mode is live

Either hit the health endpoint (anonymous, no token needed):

```
GET https://func-ordermanager-nilambur.azurewebsites.net/api/health
→ { "status": "ok", "soho": { "mode": "stub", "isPlaceholder": true, "warning": "…" } }
```

…or look for the startup line in Application Insights traces — the app logs a warning
on every cold start when it is running stubbed. Both report the SOHO client that is
actually resolved at runtime, not merely what the app setting says.

## Go-live checklist

Work through this before onboarding real client users. These are the items that are
safe during the pilot build but **not** safe with real client data.

- [ ] **Confirm `SOHO_MODE` is switched off stub before onboarding real client users.**
      Implement a real `ISohoClient` (`Lib/Soho/`) against the client's API, register
      it, and remove the `SOHO_MODE` app setting. Verify via `GET /api/health` that
      `soho.isPlaceholder` is `false`. **As part of this same step**, purge every
      placeholder order — they reference sales orders that do not exist in SOHO:
      ```sql
      -- inspect first
      SELECT id, order_number, soho_order_ref, created_at
      FROM orders WHERE soho_order_ref LIKE 'STUB%';
      -- then delete these and their dependent rows (materials, history,
      -- billing_shipping_details, order_line_items) before deleting the orders
      ```
- [ ] **Populate the `suppliers` table.** It is deliberately created empty
      (`sql/013_outsourcing.sql`) — the client's real supplier list has not been
      shared, and seeding invented names would put fake business relationships into
      the database looking exactly like real ones. Until it has rows, the
      outsourcing/import picker is empty and requests can only be raised without a
      supplier.
- [ ] Remove or rotate the `devadmin` seed account (`sql/003_seed_test_user.sql`) — its
      password is committed to this repo in plain text.
- [ ] Replace the `AllowLocalDev` SQL firewall rule with something durable, or remove it.
- [ ] Re-check role gating on workflow transitions: the seeded templates carry no
      `allowedRoles`, so any authenticated user can currently perform any legal
      transition (CLAUDE.md §5).

## Dev/test data

A standing test account is seeded via `sql/003_seed_test_user.sql` (committed,
not ad hoc):

| Username | Password | Role |
|---|---|---|
| `devadmin` | `Test@Pilot2026!` | `company_manager` (broadest access, for exercising role-gated endpoints) |

Dev/test use only — rotate or remove before onboarding real client users.
An earlier ad hoc `devadmin` insert from initial Epic 1 smoke-testing was
deleted; this one replaces it via the seed script above.

## Known gaps / follow-ups

- The `AllowLocalDev` SQL firewall rule is tied to one developer's current
  IP. Anyone else doing local development against this database will need
  their own rule added (`az sql server firewall-rule create`).
- No staging/slot environment yet — `main` deploys straight to the single
  production Function App. Revisit if a staging slot becomes necessary
  (Flex Consumption does support deployment slots).
