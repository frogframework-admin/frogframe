# Home page

The home page (`/`, `Framework.Web/Pages/Desk.razor`) shows each user what needs them today. It is built from
framework sections and from panels that applications register.

## What the user sees

| Part | Content | Shown when |
|---|---|---|
| Header | Date and greeting in the application time zone; chips for approvals waiting, overdue tasks (or tasks due today) and unread notifications; "Create new" shortcuts | Always (a shortcut only when the user may create that DocType) |
| Two-factor banner | Suggests enrolling an authenticator app | The session has no second factor (no authenticator, no single sign-on); can be hidden per user |
| Top panels | For example the ERP setup checklist | Registered with `HomeSlot.Top` |
| Needs your action | Documents waiting for the user's workflow decision, grouped by module, oldest first, with how long they have waited and the workflow buttons | At least one document waits for a decision |
| Personal panels | For example the ERP "My day" and "My requests" | Registered with `HomeSlot.Personal` |
| My Tasks | Open, due today and overdue counts; five open tasks with "Done" | Always (empty state links to `/tasks`) |
| My recent documents | Document, type, party or title, amount, status | Always (empty state offers the first shortcut) |
| Main panels | | Registered with `HomeSlot.Main` |
| Side column | Today's numbers (the "single number" widgets of the default dashboard), running background jobs, bookmarks and followed documents, side panels (for example company information) | Numbers and jobs only when there are any |

**Order.** A user with decisions waiting sees "Needs your action" first, then the personal panels. Everyone else sees
the personal panels first.

### Needs your action

- The list comes from `DocumentService.PendingActionsAsync`, the same rule as the form toolbar: role, workflow
  condition and row scope are checked.
- Left out: documents still in the workflow's initial state (the user's own drafts appear under recent documents), and
  documents whose only actions cancel them (a requester's "Withdraw", HR's "Cancel" of an approved request).
- Buttons run the workflow action with the version the row was loaded with. If the document changed meanwhile, the
  action fails with the server's message and the list reloads.
- An action marked `requireComment` in the workflow opens a reason box first; the server checks the reason again.
- The waiting time is measured from the document's last change. It is shown neutral under one day, amber from one
  day and red from three days.

### Time zone

Greeting, date, "due today" and "overdue" use `FieldTypeSettings.DefaultTimeZone` (Asia/Jakarta unless configured),
not the server clock. A container without `TZ` runs on UTC, which used to move these by seven hours. Docker Compose
also sets `TZ` for the `app` and `migrate` services (`FROGFRAME_TZ`, default `Asia/Jakarta`).

The greeting has four periods: pagi (04–10), siang (11–14), sore (15–17) and malam (18–03). In English, siang and
sore both read "Good afternoon" (translation key `Good late afternoon`).

## ERP panels (Apps.Erp)

Registered in `ErpRegistration.AddErpHome()`; data from `Apps.Erp.Home.ErpHomeService`.

| Panel | Slot | Visible to | Content |
|---|---|---|---|
| Set up FrogFrame | Top | Administrators, until every step is done or the box is hidden | Company, chart of accounts, fiscal year covering today, holiday list, users, users linked to employees, two-factor required for some roles, restore drill (ticked by hand) |
| My day | Personal | Accounts linked to an employee | Today's shift (daily schedule, else the employee's shift type), check-in and check-out times, leave balance, latest payslip with PDF link |
| My requests | Personal | Accounts linked to an employee | Latest leave, overtime, expense claim, advance and shift swap requests with their status |
| Company info | Side | Everyone, when there is something to show | Next holiday (employee's or company's holiday list, weekends skipped); colleagues of the same organization unit on approved leave today (names only) |

**Check-in from the browser.** The browser sends no GPS position, device id or selfie. The "Check in now" button is
therefore shown only when the employee's attendance mode is *Anywhere*, device binding is off and no photo is
required (HR Settings › ESS). Otherwise the panel tells the employee to use the ESS mobile app. The server checks the
same rule again before recording the punch.

Leave balances use the same calculation as the ESS API (`EssLeave.BalancesAsync`).

Quick actions registered by the ERP: leave, overtime, expense claim, shift swap, purchase request, sales invoice,
payment entry, journal entry, lead and quotation. Each appears only to users who may create that DocType; at most six
are shown.

## Adding a panel or shortcut from an application

```csharp
// A Blazor component without parameters; it injects what it needs (CurrentUser, services).
services.AddHomePanel<MyTeamPanel>("my-team", HomeSlot.Main, order: 30, visible: u => u.HasRole("Sales Manager"));

// "Create new" shortcut: shown when the DocType exists and the user may create it.
services.AddHomeQuickAction("SalesOrder", "Sales order", icon: "shopping-bag", order: 45);

// Any link, limited to roles.
services.AddHomeQuickAction(new HomeQuickAction("payables", "Payables desk", "/app/PurchaseInvoice", Roles: ["Finance-Payable"]));
```

- Panel ids are unique; registering the same id again replaces the earlier panel.
- Panels are ordered by `order`, then id. Use the `home-card`, `home-section`, `home-tile` and `home-approval-row`
  CSS classes from `desk.css` to match the framework sections.
- To let users hide a panel, store its id with `HomePreferences.DismissAsync` and check
  `HomePreferences.DismissedAsync` (user preference `home.dismissed`).

## Performance

Measured on PostgreSQL 16 with 63,000 documents (20,000 leave applications, 30,000 journal entries, 10,000 purchase
requests, 3,000 payslips), local socket, after one warm-up call:

| Section | Before | After |
|---|---|---|
| Needs your action (admin / manager / employee) | 148 / 57 / 104 ms | 2 / 3 / 3 ms |
| Recent documents (admin / manager / employee) | 98 / 32 / 39 ms | 6 / 9 / 5 ms |
| My day (employee) | 77–94 ms | 30 ms |

What changed:

- **Decision queue** (`DocumentService.DecisionQueueAsync`): only workflow states with a decision the user's roles
  allow are queried, so approved, rejected and draft documents are no longer loaded. DocTypes where the user can
  decide nothing are not queried.
- **No total counts** where only rows are needed: `StoreQuery.CountTotal = false` skips the second `count(*)` query.
  The home page, the decision queue and the ERP `Masters` helper (used by hooks) use it.
- **Parallel queries**: the DocTypes of the decision queue and of recent documents are queried six at a time; the
  page starts every section at once and shows each one as soon as its data arrives.
- **KPIs**: widgets run in parallel, a widget slower than 3 seconds is left out of the home page, and values are
  cached per user for 5 minutes (`HomeFeed.KpiCacheDuration`). The dashboard itself still shows live values.
- **ERP**: the latest payslip is one sorted query instead of up to 200 payslips; leave balances read only applications
  that can overlap the current allocation; the setup checklist checks for one employee with a user instead of reading
  500 employees.
- Hero images load lazily, so the hidden light/dark variant is not downloaded.

## Limits

- The decision queue reads up to 100 open documents per workflow DocType, oldest first.
- KPI values can be up to 5 minutes old on the home page.
