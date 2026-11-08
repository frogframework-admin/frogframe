# Secure File & Image Storage Guide

FrogFramework manages document attachments and images through a unified, secure storage subsystem (`IFileStore`).

---

## 1. Storage & Verification Engine

When files or images are uploaded via document forms:
- **Magic-Byte Sniffing**: MIME types are determined by inspecting binary headers rather than relying on user-supplied file extensions.
- **Allowed Formats**: PNG, JPEG, WebP, GIF, PDF, XLSX, DOCX, CSV.
- **Dangerous Extensions Rejected**: Executables, scripts, HTML files, and SVGs (which can contain embedded scripts causing Stored XSS) are blocked.
- **Database BLOB Persistence**: In PostgreSQL mode, files are stored in `frogframe_files`, ensuring backups and replication clusters maintain full document parity without needing shared network drives.

---

## 2. Access Control & Authorization

Uploaded assets are retrieved via `GET /files/{id}` under strict authorization rules:
1. **Unlinked Files**: If an uploaded file has not yet been attached to a document, only the original uploader can access it.
2. **Linked Documents**: Once attached to a document, file access strictly inherits the document's read permissions. Users without access to the parent document receive HTTP 404 (Not Found) to avoid leaking file existence.
3. **Cross-Tenant Isolation**: Requests across tenant boundaries are rejected.
4. **Security Headers**: All file downloads are served with `X-Content-Type-Options: nosniff` and strict Content Security Policies.

---

## 3. Orphan File Cleanup

Files that are uploaded but never linked to a saved document (e.g., cancelled form creation) are cleaned up automatically by a background worker based on `Files:OrphanHours` (default 24 hours).

