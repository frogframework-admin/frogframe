# Tags, Bookmarks and Follow

Every document form (except single DocTypes) has a row under its toolbar with **Bookmark**, **Follow** and the document's **tags**.

## Tags

Tags are shared labels: everyone who can read a document sees its tags.

- **Who can change them**: users who can edit the document (their role's write scope, or a write share), and administrators. Readers see the tags but cannot change them.
- **Adding**: type in *Add tag…* and press Enter. Tags used before on the same DocType are suggested, most used first.
- **Rules**:
  - up to 40 characters, starting with a letter or digit;
  - letters, digits, spaces and `- _ . / & #` are allowed;
  - spaces are collapsed;
  - tags compare case-insensitively, and the spelling already used on the DocType is reused ("Urgent", not "urgent" next to it);
  - at most 20 tags per document.
- **Colours**: each tag has a fixed colour derived from its name, the same everywhere.
- **History**: adding and removing a tag are written to the document's history (*Tagged* / *Untagged*).
- **Lists**: tags appear next to the document number. Clicking a tag on a form opens the list filtered by that tag (`/app/<DocType>?tag=<tag>`), shown as a *Tag = …* filter chip. Several tag filters mean "all of these tags". One tag filter returns at most 5,000 documents; narrow it with other filters for larger sets.

## Bookmarks

Bookmarks are personal: only you see yours. Use **Bookmark** on a document; **Bookmarks** in the sidebar (`/bookmarks`) lists them by DocType, newest first. A bookmark of a document you can no longer read is not shown, and bookmarks of deleted documents are removed. Up to 500 bookmarks per user.

## Follow

**Follow** on a document notifies you in the app (bell, and live while you are online) when someone else:

- updates it,
- moves it to another workflow state,
- submits, cancels or runs another action on it,
- amends it,
- comments on it (with a preview of the comment),
- deletes it (you stop following it).

You are not notified of your own changes, and not when you have lost read access to the document. If a comment mentions you, you get the mention notification only, not a second one. The *Following* tab of `/bookmarks` lists what you follow; choose **Following** on the document (or ✕ in the list) to stop. Up to 500 followers per document.

## API

| Method | Path | Notes |
|---|---|---|
| GET | `/api/data/{doctype}/{name}/social` | tags, whether you can edit them, bookmarked, following, follower count |
| POST | `/api/data/{doctype}/{name}/tags` | body `{"tag": "Urgent"}`; returns the tags |
| DELETE | `/api/data/{doctype}/{name}/tags/{tag}` | returns the tags |
| GET | `/api/tags/{doctype}?prefix=` | tags used on the DocType with counts |
| PUT / DELETE | `/api/data/{doctype}/{name}/bookmark` | bookmark / remove |
| PUT / DELETE | `/api/data/{doctype}/{name}/follow` | follow / unfollow |
| GET | `/api/bookmarks/me`, `/api/following/me` | your bookmarks / followed documents |
| GET | `/api/data/{doctype}?filters=[{"field":"_tag","op":"=","value":"Urgent"}]` | list filtered by tag; each row has `_tags` |

API keys need the `read` scope of the DocType (`update` to change tags, `list` for tag suggestions), as for other document endpoints.

## Storage

PostgreSQL tables (migration `040_social.sql`):

- `frogframe_doc_tags`: unique per document and lower-case tag, with an index on (tenant, DocType, lower-case tag) for filters and suggestions;
- `frogframe_bookmarks`;
- `frogframe_doc_follows`.

Tags, bookmarks and follows of a deleted document are removed after the delete event has been processed.
