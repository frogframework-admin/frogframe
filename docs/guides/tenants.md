# Tenants (companies of a group)

One FrogFrame installation can serve several companies of a group, for example the subsidiaries of a holding. Each
company is a **tenant**:

- it has its own users, documents, files, settings, menus, letterheads and dashboards;
- every query is filtered by the tenant id, including the reporting views;
- metadata (DocTypes, workflows, customizations) is shared by all tenants of the installation.

A user account belongs to exactly one tenant, and user names are unique across the installation. Signing in with an
account opens its tenant; there is no tenant picker. A person who works for two companies needs two accounts.
The exception is a group administrator, who can switch to any active tenant (see *Switching tenant* below).

## Group administrators

Tenants are managed at **`/platform/tenants`** (profile menu › *Manage tenants*) by the user ids listed in
`Platform:Administrators`:

```bash
# .env (Docker Compose)
FROGFRAME_PLATFORM_ADMINS=admin,group.it
```

This list is configuration on purpose: a System Manager of one company cannot make themselves administrator of the
other companies from inside the application. Keep the list short and require two-factor authentication for these
accounts (`FROGFRAME_2FA_REQUIRED_ROLES` or the tenant setting below).

## Switching tenant

A group administrator (a user id in `Platform:Administrators`) can work in any active tenant with their own account:
profile menu › **Switch tenant** (`/account/switch-tenant`), choose the tenant, *Switch*. While switched:

- the user acts as **System Manager** of that tenant: their own roles, employee record and department are not carried
  over (they belong to the home tenant), so employee-only pages such as self-service show nothing;
- a yellow bar on every page names the tenant, with a link back; *Back to my tenant* (or closing the browser) ends it;
- every switch is written to the application log (warning level: user, home tenant, target tenant);
- documents created or changed record the administrator's own user id, as usual.

How it works and what stays safe:

| Rule | Effect |
|---|---|
| Separate cookie | The choice is kept in an encrypted, HTTP-only session cookie (`frogframe.acting_tenant`) bound to the user id. The sign-in cookie is not changed; copying the cookie to another account does nothing. |
| Checked on every request | The tenant is applied only while the user is still in `Platform:Administrators` and the target tenant is active. Removing someone from the list or suspending the tenant ends it on the next request; open pages end within a minute (Blazor revalidation). |
| Browser only | API bearer tokens and API keys always act in their own tenant. |
| Form POST with antiforgery | Another site cannot switch the tenant for you. |
| Two-factor | The target tenant's "roles that must use two-factor authentication" apply to System Manager while switched. |

## Who can change metadata

Metadata (DocTypes, customizations, workflows and the DocType permissions in *Roles & Permissions*) is shared by every
tenant. With **one** registered tenant every System Manager can change it, as before. With **more than one** tenant only
group administrators can publish metadata changes, customize or reset application DocTypes, create DocTypes, publish
workflows and save DocType permissions; other System Managers see these pages read-only (they can still review a draft
and see the changes). A group administrator usually does this after switching to the tenant they are working for.

Installations where all tenants trust each other's administrators can switch this off:

```bash
# .env (Docker Compose)
FROGFRAME_TENANT_ADMINS_EDIT_METADATA=true   # = Platform:TenantAdminsCanEditMetadata
```

The same rule applies to `POST /api/admin/metadata/publish` (403). Saving a customization also checks that nobody saved
another customization of the same DocType since the editor was opened (`expectedCustomizationVersion`); otherwise the
save is refused (409) instead of overwriting the other person's custom fields.

## What a group administrator can do

| Action | Effect |
|---|---|
| **New tenant** | Creates the tenant (code, name, settings) and its first System Manager with a temporary password, shown once. The code (2–32 lower-case letters, digits, hyphens) is permanent. Nothing is created if the user name is taken or the password is rejected. |
| **Edit** | Name and settings. Saving also registers a tenant that has users but no registry entry yet. |
| **Suspend** (reason required) | Sign-in is refused, open sessions end within a minute (Blazor revalidation; every new request is refused at once), API keys get 403, scheduled jobs stop. Data is kept. You cannot suspend your own tenant. |
| **Activate** | Restores access. |
| Usage columns | Active users (and the limit), documents, last change. Counted at most every 10 minutes; *Refresh usage* recounts. |

### Settings per tenant

| Setting | Applied where |
|---|---|
| Default language | Users of the tenant who have not chosen a language (before `Localization:DefaultLanguage`). |
| Maximum active users | Creating or re-activating an account fails with "may have at most N active users". API service accounts count. Existing users are never deactivated. |
| Roles that must use two-factor authentication | Added to `Security:TwoFactor:RequiredForRoles` for this tenant's users. |
| Notes | Free text for the group administrators. |

Settings that apply to the whole installation and cannot differ per tenant yet: time zone
(`FieldTypeSettings.DefaultTimeZone`, Asia/Jakarta), metadata and customizations, e-mail sending defaults, single
sign-on providers. Metadata is edited by group administrators when there is more than one tenant (see above).

## A new company step by step

1. Create the tenant at `/platform/tenants` and send the temporary password to its administrator.
2. The administrator signs in, changes the password and turns on two-factor authentication.
3. The home page shows the setup checklist: company, chart of accounts, fiscal year, holiday list, users, employees.
4. Letterheads and print formats (`/admin/print-designs`) and menus are per tenant and start empty or with defaults.

## Where it lives

| Part | Location |
|---|---|
| Registry, status, settings, cache | `Framework.Core/Tenancy/Tenants.cs` (`ITenantStore`, `TenantDirectory`) |
| PostgreSQL store | `Framework.Persistence.PostgreSql/PostgresTenantStore.cs`, table `frogframe_tenants` (migration 045) |
| Admin service, middleware, platform admins | `Framework.Web/Tenancy/TenantAdmin.cs` |
| Page | `Framework.Web/Pages/Platform/TenantAdmin.razor` |
| Sign-in check | `ActiveUserConfirmation`, `IdentityRevalidatingAuthenticationStateProvider` |
| Scheduler | `PostgresTenantProvider` skips suspended tenants |

**Upgrading.** Migration 045 registers every tenant that already has user accounts as active, named after its id.
Rename them on the tenant page. A tenant id without a registry row keeps working as an active tenant.

**Several application instances.** A change made on one instance is sent to the others over the cluster bus; without
a cluster bus they pick it up within 30 seconds.
