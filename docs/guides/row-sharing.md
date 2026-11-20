# Row Sharing (Document Shares)

Row sharing gives specific people access to one document, next to the role-based row scope (`all`, `owner`, `match:field=attribute`). Shares are managed from the **Share** button on the document form and are stored in `frogframe_doc_shares` (migration 037).

## Rights

| Right | Effect |
|---|---|
| Read (always) | The document appears in the user's lists, reports and exports and can be opened, commented on and assigned. |
| Write | The user can edit the document while it is a draft. |
| Share | The user can share the document with others and change or remove existing shares. |

A share **widens the row scope only**. The user still needs a role that grants the DocType permission for the action (read for Read, write for Write), as with record-level sharing in other platforms. A write share given to a user whose roles only allow reading works as read-only, and a share given to a user with no role on the DocType has no effect until such a role is granted. The drawer warns about both cases.

Shares never grant delete, create, submit, cancel or other DocType-level actions. Workflow transitions still require the role named in the transition. A shared document counts as being in the user's read scope, just as a document matched by the role scope does.

## Who can share

- administrators;
- the owner of the document;
- users whose role scope lets them write the document;
- users given the **Share** right on it.

Write can only be granted by someone who can write the document. A document cannot be shared with its owner, with yourself or with a user of another tenant, and it can be shared with at most 200 users.

Anyone can leave a share given to them. Every share, change and removal is written to the document history, and a new share notifies the user. Shares are removed when the document is deleted.

## How it is enforced

- **Lists, reports, exports, home page:** `DocumentService` adds the names of the documents shared with the user (`RowScope.AnyOf.SharedNames`, at most 5,000 per DocType) to the role conditions. In PostgreSQL this becomes `... OR d.name = ANY(@shared)`.
- **Single documents** (open, update, assignments, mention notifications): `RowAccess.InScopeAsync` checks the role scope first and then the share.
- **SQL dashboards and the reporting database** query their own views and do not include shares.
