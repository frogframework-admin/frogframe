# Secure File & Image Storage Guide

FrogFramework manages document attachments and images through a unified, secure storage subsystem (`IFileStore`).

---

## 1. Storage & Verification Engine

When files or images are uploaded via document forms:
- **Magic-Byte Sniffing**: MIME types are determined by inspecting binary headers rather than relying on user-supplied file extensions.
- **Allowed Formats**: PNG, JPEG, WebP, GIF, PDF, XLSX, DOCX, CSV.
- **Dangerous Extensions Rejected**: Executables, scripts, HTML files, and SVGs (which can contain embedded scripts causing Stored XSS) are blocked.
- **Where contents live**: `Storage:Provider = Database` (default) keeps contents in PostgreSQL (`frogframe_files`, `frogframe_attachments`). `Storage:Provider = S3` keeps them in an S3-compatible object store (the docker compose setup uses SeaweedFS); see §4. Records (owner, tenant, document link, size, hash) are always in PostgreSQL.

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

When object storage is used, the cleanup also deletes the objects of the removed files.

---

## 4. Object Storage (S3-compatible)

With `Storage:Provider = S3` the **contents** of uploaded files and attachments go to a bucket; their **records** stay
in PostgreSQL, so permission checks, tenant isolation and orphan cleanup work exactly as before. Downloads still go
through the application (`GET /files/{id}`, attachment downloads), never directly to the bucket.

| Setting | Default | Meaning |
|---|---|---|
| `Storage:Provider` | `Database` | `Database` or `S3` |
| `Storage:S3:ServiceUrl` | — | Endpoint, e.g. `http://seaweedfs:8333`. Empty = AWS S3 in `Storage:S3:Region` |
| `Storage:S3:Bucket` | `frogframe` | Created at startup when missing (`Storage:S3:CreateBucket`, default true) |
| `Storage:S3:AccessKey` / `SecretKey` | — | Required |
| `Storage:S3:ForcePathStyle` | `true` | Bucket in the path; needed by SeaweedFS, MinIO and Ceph RGW without wildcard DNS |
| `Storage:MigrateExisting` | `false` (`true` in docker compose) | Moves contents still in PostgreSQL to the bucket in the background (20 per batch, one node at a time, resumable) |

Object keys: `{tenant}/files/{id}` and `{tenant}/attachments/{id}`.

Switching is safe at any time: rows written before the switch keep their content in PostgreSQL and stay readable;
new uploads go to the bucket. Switching back to `Database` is **not** automatic: contents already in the bucket would no
longer be found, so keep `S3` once files were uploaded to it.

### Which object store

The docker compose setup ships **SeaweedFS** (Apache-2.0, single container, S3 on port 8333, data in the
`frogframe-seaweedfs` volume). Any S3-compatible store works by changing `Storage:S3:*`:

- **SeaweedFS** — default; light, actively maintained, scales out to several volume servers.
- **Ceph RGW** — for organisations that already run Ceph; too heavy to bundle in docker compose.
- **MinIO** — works through the S3 API, but the community edition no longer ships binaries or Docker images
  (source only since October 2025, repository archived in 2026); only use it with a maintained build.
- **AWS S3 / other clouds** — leave `ServiceUrl` empty (AWS) or set the provider's endpoint.

### Backups

With object storage, a backup is the PostgreSQL dump **plus** the bucket (for the bundled SeaweedFS: the
`frogframe-seaweedfs` volume, or a copy with any S3 tool such as `rclone` or `aws s3 sync`). Back both up at the same
time; a record without its object is reported as "not found" on download.

