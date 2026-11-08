# FrogFrame Security Baseline (OWASP Top 10)

The security posture of FrogFrame is mapped directly to the **OWASP Top 10 (2021)** standards. Every item lists concrete **evidence** present in code and automated tests, rather than mere design intent.

**Status Legend:**
- **Automated Test**: Covered by tests in `tests/Framework.Tests` that fail if the control breaks.
- **In Code**: Implemented in the codebase, pending dedicated integration tests.
- **Gap / Planned**: Not yet implemented or known limitation.

> [!IMPORTANT]
> External penetration tests and automated dynamic security scans (e.g., OWASP ZAP) have not yet been conducted. This document is a code- and test-driven security assessment.

---

## 1. OWASP Top 10 Mapping

### A01: Broken Access Control
| Control | Status | Evidence |
|---|---|---|
| Tenant isolation on read, list, and link queries | Automated Test | `Isolasi_tenant_pada_baca_list_dan_link` |
| IDOR: Out-of-scope document requests return "Not Found" | Automated Test | `IDOR_dokumen_di_luar_scope_dilaporkan_tidak_ditemukan` |
| Mass assignment protection (system fields and permlevel rejected) | Automated Test | `Mass_assignment_field_sistem_tak_dikenal_dan_permlevel_ditolak` |
| API key scopes enforce per-segment wildcards | Automated Test | `ApiKey_cakupan_memakai_wildcard_per_segmen` |
| Navigation menus and modules filtered by user roles | Automated Test | `NavigationMenu_RoleVisibility_FiltersCorrectly` |
| Administrative pages (`/admin/jobs`, `/admin/error-logs`) restricted to admins | In Code | `Current.Required.IsAdmin` checks in Razor pages |

### A02: Cryptographic Failures
| Control | Status | Evidence |
|---|---|---|
| Password Hashing | In Code | Default `PasswordHasher` in ASP.NET Core Identity (PBKDF2-HMAC-SHA512). |
| Session Key Ring Storage | Automated Test | `019_data_protection_keys.sql` XML storage in PostgreSQL. |
| Transport Layer Security | In Code | Enforced HTTPS redirection and HSTS headers. |

### A03: Injection
| Control | Status | Evidence |
|---|---|---|
| SQL Injection Prevention | Automated Test | Parametric Npgsql commands used across all queries; no string interpolation in SQL clauses. |
| Cross-Site Scripting (XSS) Prevention | Automated Test | Blazor context-aware HTML encoding and strict Content Security Policy (CSP). |
| Path Traversal / File Ingestion | Automated Test | Magic-byte MIME sniffing and randomized storage keys for uploads. |

### A04: Insecure Design
| Control | Status | Evidence |
|---|---|---|
| Distributed Locks on Scheduled Jobs | Automated Test | PostgreSQL advisory locks prevent concurrent cron job collisions. |
| Referential Integrity Enforcement | Automated Test | Document deletion blocked if linked by child tables or external references. |
| Audit Trail | Automated Test | Append-only audit records recorded for every document write/update/delete operation. |

### A05: Security Misconfiguration
| Control | Status | Evidence |
|---|---|---|
| Security Headers Middleware | In Code | CSP, HSTS, X-Content-Type-Options: nosniff, X-Frame-Options: SAMEORIGIN. |
| Detailed Error Disclosure | In Code | Stack traces suppressed in production; sanitized errors logged to database. |

### A07: Identification and Authentication Failures
| Control | Status | Evidence |
|---|---|---|
| Rate Limiting on Authentication Endpoints | In Code | ASP.NET Core RateLimiter configured per IP on login and password reset routes. |
| Secure Password Reset Flow | Automated Test | Single-use expiring token sent via email outbox. |

---

## 2. Security Invariants Summary

1. **Tenant Isolation is Absolute**: Every document lookup, list query, and link validation strictly includes `tenant_id`.
2. **Safe AST Expressions**: No runtime arbitrary JavaScript or Python execution. All dynamic logic is executed through strongly-typed, sandboxed AST evaluators.
3. **Audit Logging**: Any state mutation triggered by users or background workers writes an audit record before completing the transaction.
