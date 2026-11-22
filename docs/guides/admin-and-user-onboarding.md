# Administrator & User Onboarding Guide

This comprehensive guide covers tenant provisioning, user lifecycle management, role governance, dynamic menu configuration, and the initial first-run company setup checklist in FrogFramework.

---

## 1. Tenant Provisioning & Platform Governance

FrogFramework supports multi-tenant operations where multiple subsidiaries or group entities share a single application and database cluster while maintaining strict logical isolation.

### 1.1 Configuring Platform Administrators

Only platform administrators defined in configuration can provision, suspend, or configure tenants. This prevents a System Manager of one subsidiary from accessing another tenant.

```bash
# In .env or container environment variables:
FROGFRAME_PLATFORM_ADMINS=admin,group.it,sre.lead
```

### 1.2 Provisioning a New Tenant

1. Sign in as a user listed in `FROGFRAME_PLATFORM_ADMINS`.
2. Navigate to **`/platform/tenants`** (from user profile menu › *Manage tenants*).
3. Click **New tenant** and configure:
   - **Tenant ID / Code**: 2–32 lower-case letters, digits, and hyphens (e.g. `acme-holding`, `corp-id`). *Note: Tenant ID is immutable once created.*
   - **Display Name**: Formal legal entity name (e.g. `PT Acme Nusantara Mandiri`).
   - **Maximum Active Users**: User account quota ceiling (prevents unauthorized license inflation).
   - **Default Language**: Default UI locale (`en-US` or `id-ID`).
   - **2FA Required Roles**: Enforce mandatory 2FA on high-privilege roles (e.g. `System Manager,HR-Payroll,Finance-Treasury`).
4. **Credential Handoff**: The dialog generates a one-time temporary password for the initial `System Manager` account.
   > [!IMPORTANT]
   > Copy and securely transmit the temporary password to the designated subsidiary administrator. It is never displayed again.

### 1.3 Tenant Status & Suspension

- **Active**: Normal transactional operations, background jobs, and API requests proceed.
- **Suspended**: All user sessions are immediately terminated, API requests receive HTTP 403, and background schedulers skip the tenant. Tenant data remains intact in PostgreSQL.

---

## 2. Initial Company Setup Checklist (First-Run Sequence)

Once the subsidiary's System Manager logs into their tenant for the first time, complete the foundational setup in the following sequence:

```
Step 1: Company Master ──► Step 2: Fiscal Year & Periods ──► Step 3: Chart of Accounts & Cost Centers
                                                                          │
Step 6: Branding & Print ◄── Step 5: HR Settings & Holiday ◄── Step 4: Organization Structure
```

| Step | DocType / URL | Action Required |
|---|---|---|
| **1. Company** | `/app/Company/new` | Set legal entity name, corporate tax ID (NPWP), default reporting currency (`IDR`), and standard domain. |
| **2. Fiscal Year** | `/app/FiscalYear/new` | Define current accounting year (e.g. `2026-01-01` to `2026-12-31`). Open child `AccountingPeriod` records. |
| **3. Accounts & Cost Centers** | `/app/Account/tree` | Load or customize Chart of Accounts. Create Cost Centers (`Department / Project`) for expense allocations. |
| **4. Organization Structure** | `/app/OrganizationUnit/new` | Build organization unit hierarchy (`Directorate` → `Division` → `Department` → `Unit`). Define `Position` titles. |
| **5. HR Settings & Holidays** | `/app/HRSettings/edit` | Set attendance cut-off dates (e.g. 21st to 20th), weekly rest days, and standard `HolidayList`. |
| **6. Letterhead & Formats** | `/admin/print-designs` | Upload company logo, set header/footer HTML templates, and customize default invoice/order print formats. |

---

## 3. User Lifecycle Management

User accounts exist at the tenant scope. Usernames must be globally unique across the installation.

### 3.1 Creating Accounts & Linking Employees

Administrators manage accounts at **`/admin/users`** or via the `User` DocType:

1. **Create User**: Specify `Username`, `Email`, and `Full Name`.
2. **Link Employee Master**: For staff members using self-service (ESS), link the `Employee` record. This enables automatic approval routing to their line supervisor.
3. **Assign Roles**: Allocate granular operational roles or broader role groups (see §4).
4. **Account Activation**: Toggle active status. The system verifies that the tenant does not exceed its configured `Maximum active users` limit.

### 3.2 Password Policy & Self-Service Reset

- **Complexity**: Minimum 12 characters required outside development environments.
- **Forgot Password Flow**: Users request password reset at `/forgot-password`. FrogFramework dispatches a secure, single-use, cryptographic token expiring in 60 minutes via the email outbox.
- **Immediate Invalidation**: Changing passwords immediately invalidates all active session cookies and API tokens.

### 3.3 Two-Factor Authentication (2FA) & SSO

#### Enforcing TOTP 2FA
- Enforce globally via `Security:TwoFactor:RequiredForRoles` or per tenant at `/platform/tenants`.
- Users scan a standard RFC 6238 TOTP QR code into Google Authenticator, Authy, or 1Password at `/account/two-factor`.
- Emergency recovery codes (hashed in database) are generated for offline account rescue.
- **Administrator 2FA Reset**: If an employee loses their authenticator device, an administrator can reset their 2FA enrollment from `/admin/users` by selecting the user and clicking **Reset 2FA**.

#### Single Sign-On (Google Workspace & Microsoft Entra ID)
Users can authenticate via organizational OAuth2/OIDC providers configured in `appsettings.json`. The user's corporate email address must match their registered FrogFramework email account.

### 3.4 Offboarding & Account Deactivation

When an employee departs:
1. In `/admin/users`, select the user and click **Deactivate**.
2. Deactivation immediately invalidates active sessions upon the next Blazor revalidation loop (< 60 seconds).
3. Open tasks and document assignments assigned to the user should be re-delegated in the `/tasks` workspace.

---

## 4. Role Governance & Segregation of Duties (SoD)

FrogFramework applies the principle of least privilege with granular roles per module (`src/Apps.Erp/ErpRoles.cs`).

### 4.1 Granular Roles vs Role Groups

To balance ease of administration with enterprise compliance:

- **Operational Role Groups**: Assigning a group automatically grants the underlying granular roles:
  - `HR`: Covers HR Admin, Recruitment, Employee Management, Time & Attendance, and Talent.
  - `Payroll`: Covers Payroll Run processing and Compensation & Benefits setup.
  - `Finance`: Covers Cashier, Receivable, Payable, Treasury, and Tax.
  - `Sales`: Covers CRM Leads, Contacts, Opportunities, Quotes, and Customer Contracts.
  - `Purchasing`: Covers Buyer workflows and Receiving.
  - `Stock`: Covers Warehouses, Stock Keepers, and Stock Opname reconciliation.

### 4.2 Segregation of Duties (SoD) Rules

For internal audit and SOX compliance, avoid combining conflicting responsibilities in single user accounts:

| Role A (Initiator) | Conflicting Role B (Approver / Custodian) | Risk Mitigated |
|---|---|---|
| `HR-Comben` (Sets Salary Assignments) | `HR-Payroll` (Executes Payroll Runs) | Unauthorized compensation inflation. |
| `Finance-Payable` (Records Vendor Invoices) | `Finance-Cashier` (Disburses Payments) | Fraudulent vendor payment release. |
| `Procurement-Buyer` (Creates Purchase Orders) | `Procurement-Receiving` (Inspects Goods) | Phantom shipment receipt. |
| `Accounting-General-Ledger` (Journal Entries) | `Accounting-Closing` (Period Closing Voucher) | Concealment of unreconciled imbalances. |

### 4.3 Permission Levels (PermLevel)

Fields within sensitive DocTypes can be assigned distinct permission levels:
- **`PermLevel = 0`**: General document fields (e.g. employee name, department, phone).
- **`PermLevel = 1`**: Confidential data (e.g. basic salary, bank account number, national identity / NIK). Restricted to designated roles (`HR-Payroll`, `HR-Comben`, `System Manager`).

### 4.4 Ad-Hoc Record Sharing (`DocSocialBar`)

Users with `can_share` privileges can grant temporary or specific document access to other users:
- Click the **Share** button on any document form.
- Grant `can_read` (viewer) or `can_write` (collaborator) access without expanding overall role boundaries.

---

## 5. Dynamic Navigation & Menu Configuration

Administrators can tailor sidebar navigation and workspace layouts per tenant and role.

### 5.1 Menu Architecture

Navigation consists of a three-level hierarchy:
1. **Modules (`/admin/modules`)**: Top-level functional domain (e.g. `Master`, `Sales`, `Purchasing`, `Stock`, `Finance`, `HR`).
2. **Menu Groups (`/admin/menu-groups`)**: Category headers inside a module (e.g. `Time & Attendance`, `Payroll Processing`, `Recruitment`).
3. **Navigation Menus (`/admin/menus`)**: Direct navigation links to DocType lists, custom report builders, or specific dashboard views.

### 5.2 Configuring Role-Based Visibility

- In **`/admin/menus`**, each menu item defines required roles.
- If a user does not possess any of the authorized roles for a menu item, the link is omitted from their sidebar navigation.
- If all items within a menu group or module are hidden from a user, the entire parent group and module are hidden automatically.

### 5.3 Desk Workspace Personalization

Each user can customize their personal desk view:
- **Pin Shortcuts**: Pin frequently accessed DocTypes or reports to the Home Desk quick-access bar.
- **Bookmarks**: Star specific document records to appear in the `/bookmarks` drawer.
- **My Tasks Widget**: Embedded on the home dashboard, displaying active assignments (`frogframe_assignments`) sorted by due date and urgency.
