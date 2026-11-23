# Position Templates & Supervisor Changes

A unit type can carry a **position template**: the job profiles and the number of positions every unit of that type gets. Opening a new branch then creates its positions automatically instead of one by one. This guide also covers what happens to supervisors and pending approvals when a manager moves or leaves.

## Position template

Set the template on the unit type (*HR → Organization Unit Type → Position template*):

| Field | Meaning |
| --- | --- |
| Job profile | Active job profile; each job profile appears once |
| Positions per unit | How many positions each unit gets (head line: always 1) |
| Head position | The unit's head; at most one line |
| Position title pattern | Optional. `{job}`, `{unit}`, `{n}`. Default `{job} {unit}`, plus ` {n}` when the quantity is above 1 |
| Worker type / Working time | Optional; otherwise the Position defaults |

Example, unit type *Cabang*: Kepala Cabang × 1 (head), Admin × 1, Sales × 5.

### New units

When a unit becomes an active unit with staffing model **Position** (created active, a *Planned* unit activated, switched to the Position model, or its unit type changed), the system creates the missing positions:

1. The head position first. It reports to the head position of the parent unit, and it becomes the unit's *Head position* when that is empty.
2. The other positions, reporting to the head position.
3. Each position is *Vacant*, has the unit's primary location, and is marked *Created from unit type template*.

The run is idempotent: every open position of a job profile in the unit counts, created by hand or not, so only the missing ones are added. A new version of an existing unit (effective-dated reorganization) is skipped: its positions move over from the previous version.

### Template changes: *Sync positions to units*

Changing a template does not touch existing units by itself. Run **Sync positions to units** on the unit type (HR-Employee-Management, HR-Admin) to:

- add positions for new lines or a higher quantity, in every active Position-model unit of the type;
- close **vacant** positions created from the template when the quantity was lowered;
- report filled positions above the template (they are never closed: move the employee first).

The action is safe to run again.

## Supervisor changes

The supervisor (*reports to*) comes from the position: the holder of *Reports to position*, else the head of the unit, walking up the parent units. Leave and expense approvers default to the supervisor.

| Event | What happens |
| --- | --- |
| Employee leaves (Employee Separation applied on the last working day) | Employee → Left, position → Vacant, user disabled. Direct reports get a new supervisor from the structure; their leave/expense approver follows when it was the leaver. Employees who had only the leaver as leave/expense approver get their own supervisor. |
| Manager changes position (Job Change applied) | Direct reports whose supervisor by structure is no longer the mover get the new supervisor; their approvers follow when they were the mover. Reports for whom the mover is still the supervisor by structure (e.g. promoted to the parent unit's head) are kept. |
| Unit head position changed | Unchanged: employees of the unit move to the new head (existing behavior). |

A supervisor set by hand to someone else is never overwritten. When no supervisor can be found (the whole chain is vacant), HR-Employee-Management gets an inbox notice: set the supervisor, or create an **Acting (Plt.)** assignment.

## Pending approvals

The daily job `Erp.ApprovalDelegationRefresh` keeps the approver of pending leave, overtime, expense claim, employee advance and purchase request documents valid:

| Approver situation | New approver |
| --- | --- |
| On approved leave | Active Acting (Plt.) for the approver or their position; without one the document waits |
| Left | Active Acting (Plt.); else the requester's current approver |
| Moved, no longer the requester's line approver | The requester's current line approver, unless the approver is an active Acting for them |

Each change is in the document's audit trail (*Approver delegated* or *Approver reassigned*), and the new approver and the requester get an inbox notice. Workflow guards compare the stored approver with the user, so the right to approve moves with the field.

Acting (Plt.) is always appointed by HR: an **Employee Assignment** with type *Acting*, *Acting for* (employee) or *Position*, start and end date, status *Active*, and approval authority.
