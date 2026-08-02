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

## Complete configuration reference

Every setting the system needs, and where each one lives. **Values are not recorded
here** — read them with `az functionapp config appsettings list --name
func-ordermanager-nilambur -g rg-ordermanager-nilambur`.

Three locations, and they are not interchangeable:

| Location | Used by | Committed? |
|---|---|---|
| Function App application settings | the deployed app | no — set in Azure |
| `local.settings.json` | local `func start` only | **no, gitignored** |
| GitHub Actions repo secrets | CI deployment only | no — set in GitHub UI |

### Function App settings (deployed) + `local.settings.json` (local dev)

The app reads these identically in both places, so the same name must exist in both for
local development to work.

| Setting | Purpose | Status |
|---|---|---|
| `SQL_CONNECTION_STRING` | Connection to `sqldb-ordermanager`. Contains the `sqladmin` password. | Real |
| `JWT_SECRET` | HS256 signing key; 12h token lifetime (contract §2). | Real — but generated for the pilot and never rotated. See below. |
| `CLIENT_ID` | `c6c944a9-b531-4c21-a3fd-9a8d6df2b180`. Selects which `process_templates` / `production_step_templates` row the engine loads. Not a secret — the tenant key for the multi-client design. | Real |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Points at `appi-ordermanager-nilambur`. **Required** — the .NET 10 isolated worker template wires OpenTelemetry at startup and will not start without it. | Real |
| `PHOTO_STORAGE_CONNECTION_STRING` | `stordermgrphotosnilambur`. Contains the account key, which is what lets the app mint SAS tokens — it cannot be swapped for a connection string without one. | Real |
| `PHOTO_CONTAINER_NAME` | `production-photos`. | Real |
| `SOHO_MODE` | `stub` — activates the placeholder SOHO client. | ⚠ **PLACEHOLDER. Must be removed before go-live.** |
| `AzureWebJobsStorage` | Functions runtime state. Set by provisioning. | Real |
| `DEPLOYMENT_STORAGE_CONNECTION_STRING` | Flex Consumption deployment packages. Set by provisioning; **deployed app only**, not needed locally. | Real |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated`. **Local only** — the deployed app carries this in its Flex Consumption runtime config instead of app settings. | Real |

**Nothing needs adding for push notifications.** Expo requires no credentials on this
side; Firebase/Apple credentials live in the mobile repo via EAS.

**When the real SOHO client is written** it will likely need its own settings (base URL,
API key or OAuth details). Add them here at that point.

### GitHub Actions repo secrets (CI only)

Set under Settings → Secrets and variables → Actions. None is secret in the
password sense — OIDC means there is no client secret — but all three are scoped to
exactly one resource group by the role assignment.

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | `b03888c3-6234-4bd6-85a1-d236689ee261` |
| `AZURE_TENANT_ID` | `bee4b7aa-ef0b-47a3-8b1b-986b63440ad1` |
| `AZURE_SUBSCRIPTION_ID` | `761807b1-4e09-4429-ae58-4419e856128b` |

### Secrets with no rotation story

Worth knowing before go-live, though none is a checklist blocker on its own:

- **`JWT_SECRET`** — rotating it invalidates every issued token at once, forcing all
  users to log in again. There is no dual-key grace period. Acceptable for a pilot with
  12-hour tokens; revisit if that becomes disruptive.
- **The SQL admin password** exists only inside `SQL_CONNECTION_STRING`. Rotating it means
  updating the setting and redeploying.
- **The photo storage account key** likewise lives inside its connection string, and
  rotating it invalidates any SAS tokens already issued (max ~15 minutes of impact).

### Push notifications add no Azure resources

Push goes through the **Expo Push API** (`https://exp.host/--/api/v2/push/send`), an
outbound HTTPS call needing no credentials — so notifications introduced **no** Azure
resource and nothing to provision or pay for. The originally planned Azure Notification
Hubs instance was never created and is not needed: Expo already brokers to FCM/APNs, so
Hubs would have been a second broker in front of the first.

Firebase and Apple credentials live in the mobile repo, configured via EAS. If push
fails for an entire platform, that is where to look — there is no backend configuration
governing it.

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

**The single authoritative list of what must happen before real client users touch this
system.** Everything below is safe during the pilot build and **not** safe with real
client data. If a note about pre-go-live work exists elsewhere in the docs, it should
also appear here — this list is the one people will actually check.

Ordered by blast radius: the first two put fabricated data in front of staff, the rest
are access and hygiene.

### 1. Replace the SOHO stub — BLOCKING

SOHO is not a real integration. `SOHO_MODE=stub` is currently set on
`func-ordermanager-nilambur`, so **every customer order created so far carries an
invented sales order reference**.

- [ ] Implement a real `ISohoClient` (`Lib/Soho/`) against the client's API and register
      it in `Program.cs`.
- [ ] Remove the `SOHO_MODE` app setting so the safe default applies (an unconfigured
      SOHO rejects customer orders with `503` rather than inventing references).
- [ ] Verify: `GET /api/health` returns `soho.isPlaceholder: false`.
- [ ] Purge placeholder orders — they reference sales orders that do not exist in SOHO:
      ```sql
      -- inspect first
      SELECT id, order_number, soho_order_ref, created_at
      FROM orders WHERE soho_order_ref LIKE 'STUB%';
      -- then delete dependent rows (materials, order_line_item_steps,
      -- line_item_status_history, order_status_history, billing_shipping_details,
      -- order_line_items) before deleting the orders themselves
      ```
- [ ] Re-check customer order numbering. It is `CUS-` + SOHO's number verbatim; if their
      real format carries its own prefix the result doubles up (`CUS-SO-4471`). See
      `Lib/OrderNumberFormatter.cs`.

### 2. Populate reference data — BLOCKING

Both tables are deliberately empty. Inventing rows would put fabricated business data in
front of staff looking exactly like the real thing.

- [ ] **`suppliers`** (`sql/013_outsourcing.sql`) — until populated, the
      outsourcing/import picker returns nothing and requests can only be raised without
      a supplier.
- [ ] Confirm `stores` still reflects reality (currently Kochi and Bangalore).

There is deliberately **no inventory table** — inventory is derived from stock orders
and needs no seeding. See CLAUDE.md §8a; do not "fix" this by adding one.

### 3. Accounts and access

- [ ] **Remove or rotate the `devadmin` account** (`sql/003_seed_test_user.sql`). Its
      password is committed to this repo in plain text, and it holds `company_manager`.
- [ ] **Create real user accounts** by following the runbook below. Accounts are
      provisioned manually by design for this pilot — a user-management portal is
      planned for a later phase, so there are deliberately no user CRUD endpoints.
      - Role gating is enforced on actions, so **a user with no roles can log in but do
        almost nothing**. Assign roles deliberately.
      - There is **no password reset endpoint**. A forgotten password is handled by
        re-running step 1 of the runbook and updating the row.
- [ ] Replace the `AllowLocalDev` SQL firewall rule with something durable, or remove it.
      It is pinned to one developer's IP at one moment in time.

### 4. Push notifications

- [ ] Confirm Firebase/Apple credentials are configured in the **mobile repo via EAS**
      (`eas credentials`). Nothing is needed in this repo — Expo brokers delivery — but
      push silently fails for a whole platform if they are missing.
- [ ] Confirm `notification_recipients` routes each event to the right people. Routing is
      data: an event with no active rows notifies nobody while everything else reports
      success.

### 5. Operational

- [ ] Consider a staging environment. `main` currently deploys straight to the single
      production Function App.
- [ ] Remember: **template changes require a redeploy** to take effect, and editing a
      template without redeploying causes instances to disagree. See the caching note
      above.

## Runbook: creating a user account

Accounts are provisioned by hand for this pilot. A user-management portal is planned as
future work; until then this is the process, and it needs to be followable by whoever
handles onboarding.

Requires: access to the SQL database (see the firewall note above) and any machine with
Python or .NET for step 1.

### Step 1 — generate a password hash

Passwords are stored as bcrypt hashes, **work factor 12** (see `Lib/PasswordHasher.cs`).
Never insert a plain-text password.

Using Python (verified compatible with the BCrypt.Net library the app uses):

```bash
pip install bcrypt          # once
python -c "import bcrypt; print(bcrypt.hashpw(b'THE-PASSWORD-HERE', bcrypt.gensalt(12)).decode())"
```

Output looks like `$2b$12$vmy8ixUD0WaM7bKrdLYGxe...`. Copy it whole, including the
`$2b$12$` prefix.

Give the password to the user over a channel that is not this hash, and have them treat
it as theirs — there is no self-service change flow yet.

### Step 2 — insert the user and their roles

```sql
DECLARE @userId UNIQUEIDENTIFIER = NEWID();

INSERT INTO users (id, username, password_hash, email, first_name, last_name, mobile_no, active)
VALUES (@userId,
        'jdoe',                      -- must be unique
        '$2b$12$...',                -- the hash from step 1
        'jdoe@example.com',          -- optional
        'Jane', 'Doe',
        '9876543210',                -- optional
        1);

-- One row per role. A user may hold several.
INSERT INTO user_roles (user_id, role) VALUES (@userId, 'salesperson');
```

Valid roles — **assign deliberately, because actions are gated on them**:

| Role | Can do |
|---|---|
| `salesperson` | Raise orders (`NEW → IN_PRODUCTION`); sees only their own orders |
| `factory_supervisor` | All production steps, moving orders out of production, dispatch to transit; sees all orders but only raw-material requests they raised |
| `store_manager` | Invoicing handoff, all store-side movements, raw-material procurement; sees all orders |
| `company_manager` | Everything `store_manager` does, plus outsourcing/import |

A user with **no** roles can log in and read, but cannot perform almost any state change.

### Step 3 — verify

```bash
curl -s -X POST https://func-ordermanager-nilambur.azurewebsites.net/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"jdoe","password":"THE-PASSWORD-HERE"}'
```

A `200` with a token and the expected `roles` array confirms both the hash and the role
rows. A `401` means the hash does not match — regenerate it rather than guessing.

### Deactivating someone

Set `active = 0` on the `users` row. Login is refused immediately; existing tokens
remain valid until they expire (up to 12 hours), as tokens are not checked against the
database per request.

```sql
UPDATE users SET active = 0 WHERE username = 'jdoe';
```

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

Anything blocking go-live lives in the **Go-live checklist** above, not here — this
section is for things that are merely inconvenient, so the checklist stays the one place
worth checking.

- The `AllowLocalDev` SQL firewall rule is tied to one developer's current
  IP. Anyone else doing local development against this database will need
  their own rule added (`az sql server firewall-rule create`).
- No staging/slot environment yet — `main` deploys straight to the single
  production Function App. Revisit if a staging slot becomes necessary
  (Flex Consumption does support deployment slots).
