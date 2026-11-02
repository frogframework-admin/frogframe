# DocFrame Security Baseline & OWASP Top 10 Verification

Dokumen evaluasi dan standar keamanan produksi untuk DocFrame, merujuk pada **OWASP Top 10 Web Application Security Risks**.

---

## 1. Pemetaan Mitigasi OWASP Top 10

| Kategori OWASP | Risiko | Mitigasi di DocFrame | Status |
|---|---|---|---|
| **A01: Broken Access Control** | Bypass otorisasi, kebocoran data lintas tenant (IDOR) | • Multi-tenant enforced pada query (`WHERE tenant_id = @tenantId`).<br>• PermLevel & field-level permissions dicek pada setiap operasi baca/tulis.<br>• Token API dibatasi segmen wildcard (`doc:PurchaseRequest:read`).<br>• Menu & Module terisolasi per role. | **Terverifikasi** (Unit + PG tests) |
| **A02: Cryptographic Failures** | Kebocoran kredensial atau kunci sesi | • Password di-hash dengan ASP.NET Core Identity (PBKDF2-HMAC-SHA256, 100k iterasi).<br>• Kunci Data Protection dienkripsi dan dipersistensi di PostgreSQL (`docframe_data_protection_keys`, P0-2).<br>• Token API hanya disimpan dalam bentuk SHA-256 hash (`dfk_<id>.<secret>`). | **Terverifikasi** |
| **A03: Injection** | SQL Injection, XSS, NoSQL Injection | • Akses database menggunakan 100% kueri berparameter (*parameterized queries*) via Npgsql.<br>• Filter dan sort hanya diizinkan dari allowlist field terkompilasi.<br>• GIN JSON query menggunakan containment berparameter (`@>`).<br>• Input HTML di-sanitize pada Blazor rendering. | **Terverifikasi** |
| **A04: Insecure Design** | Kurangnya proteksi arsitektur | • Pola transactional outbox menjamin konsistensi dokumen dan event.<br>• Distributed advisory lock (`pg_try_advisory_lock`) mencegah eksekusi duplikat scheduled job.<br>• Cek integritas referensial link sebelum delete/cancel (P0-4). | **Terverifikasi** |
| **A05: Security Misconfiguration** | Header tidak aman, informasi sistem bocor ke publik | • `SecurityHeadersMiddleware`: HSTS 365 hari, `X-Content-Type-Options: nosniff`, `X-Frame-Options: SAMEORIGIN`, CSP global.<br>• RFC 7807 problem+json terpadu (stack trace dan detail internal disembunyikan dari klien publik).<br>• Endpoint `/health/live` dan `/health/ready` hanya mengembalikan status biner tanpa error database. | **Terverifikasi** (P0-3) |
| **A06: Vulnerable & Outdated Components** | Paket pihak ketiga yang memiliki celah CVE | • `Framework.Core` memiliki **zero external dependencies** (hanya .NET 10 BCL).<br>• Pemindaian `dotnet list package --vulnerable` menghasilkan **0 celah keamanan**. | **Terverifikasi** |
| **A07: Identification & Auth Failures** | Brute force password, pembajakan sesi | • Fitur lockout otomatis aktif setelah 5 kali percobaan gagal berturut-turut.<br>• Akun nonaktif langsung mencabut validitas semua token API dan sesi cookie.<br>• Perubahan role segera meregenerasi `SecurityStamp`. | **Terverifikasi** |
| **A08: Software & Data Integrity Failures** | Integritas data rusak karena race condition atau manipulasi | • Optimistic concurrency control via kolom `version`.<br>• Validasi server ketat independen dari UI.<br>• Serial number unik atomik dengan `pg_advisory_xact_lock`. | **Terverifikasi** |
| **A09: Security Logging & Monitoring Failures** | Kegagalan mendeteksi aktivitas mencurigakan | • Audit trail lengkap untuk setiap operasi `Insert`, `Update`, `Submit`, `Cancel`, dan `Delete`.<br>• Logging error terpadu (`IErrorLogStore` + tabel `docframe_error_logs`).<br>• OpenTelemetry metrics (`DocFrameTelemetry`) untuk request, error, job, dan outbox. | **Terverifikasi** (P0-6) |
| **A10: Server-Side Request Forgery (SSRF)** | Permintaan keluar sewenang-wenang dari server | • Ekstensi visual hook dan webhook membatasi domain tujuan ke daftar allowlist resmi.<br>• Format eksternal diverifikasi sebelum proses. | **Terverifikasi** |

---

## 2. Review Dependensi Solusi
Hasil pemindaian keamanan dependensi paket resmi via NuGet Security Advisory per November 2026:

```
The given project `Framework.Core` has no vulnerable packages given the current sources.
The given project `Framework.Persistence.PostgreSql` has no vulnerable packages given the current sources.
The given project `Framework.Web` has no vulnerable packages given the current sources.
The given project `Apps.Sample` has no vulnerable packages given the current sources.
The given project `Framework.Tests` has no vulnerable packages given the current sources.
```

Semua dependensi berada pada versi terkini yang didukung penuh oleh vendor (Microsoft .NET 10 LTS & Npgsql).
