# Document Assignments (Assign To / ToDo) & My Tasks

FrogFramework provides built-in document delegation ("Assign To"), due-date tracking, daily reminders, and a **My Tasks** workspace so teams can hand off documents without leaving the record.

---

## Key Capabilities

- **Assign from Any Document**: Open any saved document and click **Assign** in the toolbar (or click the assignee strip in the document header) to assign one or more users with a **Due Date**, **Priority** (`Low`, `Medium`, `High`), and **Note**.
- **Strict Permission & Scope Enforcement**:
  - Any user who can read a document within their row scope can assign it.
  - Only active users within the same tenant who also have `read` permission and row-level scope on the document can be assigned, ensuring assignments never leak inaccessible documents.
  - Only one open assignment per user per document is allowed (duplicate open assignments are skipped cleanly).
- **Audit Trail Integration**: Every assignment creation, completion, cancellation, and edit writes an entry to the document's **History & Comments** timeline.
- **Real-Time Notifications & Daily Reminders**:
  - Outbox handler `AssignmentNotifier` listens to `document.assignment` events and pushes instant in-app notifications (and live SignalR updates) to assignees and assigners.
  - Optional email delivery can be enabled via `Assignments:SendEmail = true`.
  - Scheduled job `AssignmentReminderJob` (`@daily`) sends at most one reminder per day for tasks due today or overdue.
  - `AssignmentDocumentCleanupHandler` automatically cancels open assignments when a document is deleted.
- **My Tasks Page (`/tasks`) & Home Widget**:
  - Dedicated `/tasks` inbox with **Open**, **Due Today**, **Overdue**, **Completed**, and **Assigned by Me** tabs, search, sorting, and Load More pagination (`Ui:TasksPageSize`).
  - Live open-task badge on the sidebar navigation.
  - **My Tasks** widget on the Home desk showing Open / Due Today / Overdue counters and the 5 nearest-due tasks with quick **Done** completion.

---

## Database Schema

Migration `036_assignments.sql` creates `frogframe_assignments` with optimistic concurrency (`version`) and a partial unique index preventing duplicate open assignments per `(tenant_id, doctype, doc_name, LOWER(assigned_to))`.

---

## REST API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/assignments/me` | List tasks assigned to the current user (`status`, `overdue`, `dueToday`, `search`, `skip`, `take`). |
| `GET` | `/api/assignments/me/counts` | Return `{ open, dueToday, overdue }` counters for the current user. |
| `GET` | `/api/data/{doctype}/{name}/assignments` | List assignments on a document (requires `read` API operation and document scope). |
| `POST` | `/api/data/{doctype}/{name}/assignments` | Assign a document (`{ assignees, dueDate, priority, description }`). |
| `POST` | `/api/assignments/{id}/complete` | Mark an open assignment complete (`{ note }`). |
| `POST` | `/api/assignments/{id}/cancel` | Cancel or decline an open assignment (`{ note }`). |
| `PATCH` | `/api/assignments/{id}` | Update `dueDate`, `priority`, `assignedTo`, or `description` (assigner or System Manager). |
