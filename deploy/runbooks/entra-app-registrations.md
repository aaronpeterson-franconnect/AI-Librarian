# Entra ID app registrations — AI Librarian

Microsoft Entra (Azure AD) **app registrations are not modeled as first-class
Azure Resource Manager resources**. Provisioning them via Bicep would require a
`deploymentScripts` resource plus Microsoft Graph permissions, which adds
operational surface area we do not want in Phase 0. Instead, create the
following registrations manually (or via your existing IAM automation) and
store client secrets / federated credentials in Key Vault after the Bicep
deployment completes.

Per [ADR 0005](../../docs/adr/0005-rls-with-entra.md) and
[ADR 0004](../../docs/adr/0004-mcp-as-single-access-layer.md), the v1 footprint
expects **three** first-party applications:

| Application | Purpose | Token audience |
|---|---|---|
| **AI Librarian API** | Issues the primary REST API (`AiLibrarian.Api`). | `api://<api-app-id>` |
| **AI Librarian Portal** | SPA or Blazor WASM front-end (future). | API delegated permissions |
| **AI Librarian MCP** | Model Context Protocol server used by Cursor, Copilot, etc. | API delegated permissions |

## 1. Register the API application

1. Entra admin center → **Applications** → **App registrations** → **New registration**.
2. Name: `AI Librarian API` (or your naming standard).
3. Supported account types: **Accounts in this organizational directory only**.
4. Register — note the **Application (client) ID** and **Directory (tenant) ID**.

Expose an API:

1. **Expose an API** → **Add** application ID URI → `api://<client-id>`.
2. Add scopes (examples for v1):
	- `access_as_user` — delegated access for users and client apps calling on behalf of a user.
3. Under **Authorized client applications**, add the Portal and MCP client IDs
	once they exist (step 2 and 3).

## 2. Register the Portal application

> **Phase 1.5 status:** the Portal is server-rendered Blazor Server.
> The portal app registration uses platform = **Web** with the
> standard OIDC redirect URIs. Single-page-app (`/auth-redirect`) flow
> is not used.

1. New registration → name `AI Librarian Portal`.
2. Platform: **Web**.
3. Redirect URIs (one per environment):
	- `https://portal-dev.example.com/signin-oidc`
	- `https://portal-stage.example.com/signin-oidc`
	- `https://portal.example.com/signin-oidc`
	- `https://localhost:7000/signin-oidc` for local dev runs.
4. Front-channel logout URL: `https://<portal-host>/signout-oidc`.
5. **Implicit grant**: leave both ID token and access token **unchecked**
	(we use confidential-client OBO, not implicit flow).
6. **Certificates & secrets** → create a client secret. Stash in Key
	Vault as `Portal--AzureAd--ClientSecret` (Microsoft.Identity.Web
	binds it from `AzureAd:ClientSecret`).
7. **API permissions** → add permission → **My APIs** → AI Librarian
	API → Delegated → `access_as_user`. Grant admin consent.
8. Wire `appsettings.json` (or environment variables) on the Portal:

	```jsonc
	"AzureAd": {
		"Instance": "https://login.microsoftonline.com/",
		"TenantId": "<tenant-guid>",
		"ClientId": "<portal-app-client-id>",
		"ClientSecret": "<from key vault>",
		"CallbackPath": "/signin-oidc",
		"SignedOutCallbackPath": "/signout-callback-oidc"
	},
	"DownstreamApi": {
		"Scopes": [ "api://<api-app-client-id>/access_as_user" ]
	}
	```

	Leaving `AzureAd:ClientId` empty keeps the Portal in dev-mode
	(DevContributors dropdown). Setting it flips the Portal into
	OIDC sign-in, the `LoginDisplay` component swaps to a real
	Sign in / Sign out link, the `AuthenticationDelegatingHandler`
	starts attaching a bearer token to every API call, and the
	upload form hides the contributor input (the API resolves the
	contributor from the bearer's `oid` claim).

9. **Provision the user in `users`.** The API joins on the `oid`
	claim, so the contributor must already exist in the `users` table
	with `external_id = <oid>`. Phase 1's group-sync job seeds this
	from Entra group membership.

## 3. Register the MCP application

1. New registration → name `AI Librarian MCP`.
2. Depending on MCP host auth model (daemon vs on-behalf-of-user):
	- **Public client** if interactive device-code / browser login per user.
	- **Confidential client** if the MCP server runs in Azure with a managed identity
		or client secret (prefer federated workload identity for production).
3. **API permissions** → delegated permissions to AI Librarian API as needed.

## 4. Map Entra groups to `user_authorizations`

Create Entra security groups per department and role ladder (Reader,
Contributor, Reviewer, Librarian) per
[ADR 0005](../../docs/adr/0005-rls-with-entra.md). Document the group
object IDs in your CMDB; they become the `source_group_id` column.

### 4a. JIT user provisioning (Phase 1.5)

The API auto-creates the `users` row the first time a new OID signs
in. This happens in `SessionContextResolver` → `IUserDirectory.EnsureUserAsync`,
gated by the `p_users_self_insert` / `p_users_self_update` RLS policies
from `db/changelog/0101-users-self-provisioning.sql`. No operator
action needed — the user shows up in `users` automatically after their
first authenticated request.

The bearer is the authority on every sign-in:

| Claim | Column |
|------|--------|
| `oid` | `users.id` (also the primary key) |
| `preferred_username` / `upn` / `email` | `users.email` |
| `name` | `users.display_name` |
| `idtyp = user` AND `acct ≠ 1` | `users.is_employee = true` |

If any of these change in Entra, the next sign-in refreshes the
database row (UPSERT with COALESCE on the optional fields).

### 4b. Role grants — manual bootstrap path

The Entra-group-sync job (see 4e) auto-reconciles role grants from
group membership. The bootstrap path below is for **before** the
sync job is configured — typically the operator's own grants so they
can sign in as Admin and configure the sync job from there.

Use `deploy/scripts/Bootstrap-UserAuthorizations.ps1` to grant roles:

```powershell
# Pilot Engineer becomes a Librarian for the Engineering department
.\deploy\scripts\Bootstrap-UserAuthorizations.ps1 `
    -UserOid 22222222-2222-2222-2222-222222222222 `
    -Role Librarian `
    -DepartmentId 11111111-1111-1111-1111-111111111111

# System-wide Admin (no DepartmentId)
.\deploy\scripts\Bootstrap-UserAuthorizations.ps1 `
    -UserOid 33333333-3333-3333-3333-333333333333 `
    -Role Admin
```

The script:

- Connects with the Postgres admin role (`$env:ConnectionStrings__Postgres`)
  which has BYPASSRLS implicit — required to write to
  `user_authorizations` (`p_user_auth_write` is Admin-only).
- Validates `Role` is one of the five lattice values and enforces
  the schema's `chk_user_auth_admin_no_dept` invariant (Admin must
  be system-wide; non-Admin requires a department).
- Idempotent — re-running the same grant is a no-op via the unique
  index. Use `-DryRun` to preview the SQL.
- Tags every grant with a `source_group_id` label (default
  `bootstrap`). When the Phase 2 group-sync job lands, it can detect
  rows whose `source_group_id` starts with `bootstrap-` and decide
  whether to reconcile them.

The grant takes effect on the user's **next** API call — the
SessionContextResolver re-reads `user_authorizations` via
`PostgresUserDirectory.GetProjectionAsync` per request (with a small
scoped cache for the duration of one request).

### 4c. Flipping the Portal into Entra mode

After the API + Portal app registrations exist and the operator has
granted themselves at least one role:

1. Update `src/AiLibrarian.Portal/appsettings.json` (or per-environment
   config) with `AzureAd:TenantId` + `AzureAd:ClientId` + `AzureAd:ClientSecret`
   and `DownstreamApi:Scopes`.
2. Restart the Portal. The `LoginDisplay` component flips from the
   dev-mode contributor label to a Sign in / Sign out link.
3. Sign in. The first request lands in `SessionContextResolver`:
   - `EnsureUserAsync` upserts the row in `users`.
   - `GetProjectionAsync` returns the bootstrap grants from step 4b.
   - The RLS session push carries real department / role arrays.
4. Upload a file. The contributor input is hidden; the API resolves
   the contributor from the bearer's `oid` claim.

### 4d. Pre-bootstrap edge case

If a user signs in before any `user_authorizations` row exists for
them, they will:

- Have a `users` row (JIT-provisioned).
- See **only Public sources** via RLS — Reader on a department is the
  minimum to see Internal+.
- Be rejected from upload / approve / etc. routes that require
  Contributor or higher.

This is the correct fail-closed behavior. Run
`Bootstrap-UserAuthorizations.ps1` to fix.

### 4e. Entra group-sync (Phase 1.5)

Once the operator has at least one Admin grant via 4b, the periodic
group-sync job replaces manual `Bootstrap-UserAuthorizations.ps1`
invocations. The sync runs as a hosted service in the API
(`EntraGroupSyncHostedService`) plus an on-demand admin endpoint
(`POST /api/admin/entra-sync`).

#### Microsoft Graph permission

Grant the **API's** app registration (not the Portal's) one
application permission on Microsoft Graph:

| Permission | Type | Why |
|------------|------|-----|
| `GroupMember.Read.All` | Application | List members of every declared Entra group |

In Entra → App registrations → AI Librarian API → **API permissions**:

1. **Add a permission** → Microsoft Graph → **Application permissions**.
2. Search `GroupMember.Read.All` → check it → **Add permissions**.
3. Click **Grant admin consent for &lt;tenant&gt;** — admin consent is
   required for application permissions.

Then create a client secret (or use a federated credential — preferred
for production) on the API's app registration. Stash in Key Vault as
`Api--EntraSync--ClientSecret`.

#### Configure the sync job

Append to `src/AiLibrarian.Api/appsettings.json` (or environment-
specific config / Key Vault references):

```jsonc
"EntraSync": {
    "Enabled": true,
    "TenantId": "<tenant-guid>",
    "ClientId": "<api-app-client-id>",
    "ClientSecret": "<from key vault>",
    "Interval": "00:15:00",
    "GroupMappings": [
        {
            "GroupObjectId": "<entra group id for engineering librarians>",
            "DisplayLabel": "Engineering Librarians",
            "Role": "Librarian",
            "DepartmentId": "11111111-1111-1111-1111-111111111111"
        },
        {
            "GroupObjectId": "<entra group id for engineering readers>",
            "DisplayLabel": "Engineering Readers",
            "Role": "Reader",
            "DepartmentId": "11111111-1111-1111-1111-111111111111"
        },
        {
            "GroupObjectId": "<entra group id for ai-librarian admins>",
            "DisplayLabel": "AI Librarian Admins",
            "Role": "Admin"
        }
    ]
}
```

Rules the orchestrator enforces upfront (invalid mappings skip with a
warning rather than failing the whole run):

- `Role: Admin` requires an **empty** `DepartmentId`.
- Every other role requires a non-empty `DepartmentId` GUID.
- `GroupObjectId` must be a GUID.

#### How reconciliation works

Every interval (or on demand via the admin endpoint), per mapping:

1. Call `GET /v1.0/groups/{id}/members/microsoft.graph.user` against
   Graph (paginates via `@odata.nextLink`; service principals + nested
   groups filtered out).
2. For each member OID: upsert a `user_authorizations` row tagged with
   `source_group_id = "entra-sync:<group-guid>"`.
3. Delete every `user_authorizations` row with that
   `source_group_id` whose `user_id` is **not** in the current
   membership — the revoke path.
4. Emit one `user_auth/sync.group` audit row per mapping with non-zero
   changes; one `user_auth/sync.run` audit row per overall pass.

**Bootstrap grants are safe.** Manual rows from
`Bootstrap-UserAuthorizations.ps1` carry `source_group_id = "bootstrap"`
(or any operator-supplied label), so the sync job's reconciliation
never touches them. The sync only owns rows whose
`source_group_id` starts with `entra-sync:`.

#### Triggering on demand

Sign in as Admin via the Portal (or any caller with an Admin
authorization), then:

```powershell
$token = "<bearer from your sign-in>"
Invoke-RestMethod `
    -Method Post `
    -Uri "https://<api-host>/api/admin/entra-sync" `
    -Headers @{ Authorization = "Bearer $token" }
```

Returns a `SyncReport` JSON: per-mapping member count + grants
added / revoked + an overall `groups_failed` count. Useful right
after editing `GroupMappings` config to validate the change.

#### Failure modes

- **Graph 401/403** — service principal lacks `GroupMember.Read.All`
  or admin consent isn't granted. Fix in Entra; no API restart needed.
- **Network / 5xx from Graph** — per-mapping isolation means other
  groups still reconcile. Per-mapping `Error` field surfaces in the
  `SyncReport`.
- **Stale config** — change to `EntraSync:GroupMappings` requires API
  restart (config is bound at startup). The admin endpoint then
  reflects the new mappings.

#### Phase 2 transition

When the Phase 2 wiki schema lands, the group-sync job stays as-is.
The wiki-domain code calls into the same `IUserAuthorizationWriter` /
`IUserDirectory` interfaces — no churn at the role-resolution layer.

## 5. Store secrets in Key Vault

After `deploy/bicep/main.bicep` deploys Key Vault:

- Portal client secret (if confidential) — or configure federated credentials.
- MCP client secret (if confidential).
- PostgreSQL connection string (until Entra auth for Postgres is enabled).
- Azure OpenAI keys / endpoints (until managed identity + RBAC is wired).

Never commit these values to git. CI should inject them at deploy time or use
Key Vault references from Container Apps.

## 6. Wire `appsettings` / Container Apps

The API reads `AzureAd:TenantId`, `AzureAd:ClientId`, `AzureAd:Audience` per
`src/AiLibrarian.Api/appsettings.json`. Set these from the deployed Key Vault
or Container Apps secret environment variables.
