# DocFrame Security Baseline (OWASP Top 10)

Status keamanan DocFrame dipetakan ke **OWASP Top 10 (2021)**. Setiap baris menyebut **bukti** yang benar-benar ada, bukan sekadar niat desain.

**Arti status:**
- **Diuji otomatis**: ada test di `tests/Framework.Tests` yang gagal bila kontrol ini rusak.
- **Ada di kode**: implementasi ada, tapi belum ada test khusus.
- **Celah / belum**: belum ada, atau ada kelemahan yang diketahui.

> [!IMPORTANT]
> Belum ada pemindaian dinamis (OWASP ZAP) maupun penetration test. Dokumen ini review berbasis kode dan test, bukan audit eksternal.

---

## 1. Pemetaan OWASP Top 10

### A01: Broken Access Control
| Kontrol | Status | Bukti |
|---|---|---|
| Isolasi tenant pada baca, list, dan link | Diuji otomatis | `Isolasi_tenant_pada_baca_list_dan_link` |
| IDOR: dokumen di luar scope dilaporkan "tidak ditemukan" | Diuji otomatis | `IDOR_dokumen_di_luar_scope_dilaporkan_tidak_ditemukan` |
| Mass assignment (field sistem, permlevel) ditolak | Diuji otomatis | `Mass_assignment_field_sistem_tak_dikenal_dan_permlevel_ditolak` |
| Cakupan API key per segmen, endpoint tanpa cakupan menolak API key | Diuji otomatis | `ApiKey_cakupan_memakai_wildcard_per_segmen` |
| Menu dan modul admin tersembunyi dari non-admin | Diuji otomatis | `NavigationMenu_RoleVisibility_FiltersCorrectly` |
| Halaman `/admin/jobs` dan `/admin/error-logs` hanya untuk admin | Ada di kode | Cek `Current.Required.IsAdmin` di halaman Razor; belum ada test UI |

### A02: Cryptographic Failures
| Kontrol | Status | Bukti |
|---|---|---|
| Hash password | Ada di kode | Default `PasswordHasher` ASP.NET Core Identity (format V3: PBKDF2-HMAC-SHA512). Tidak ada override iterasi. |
| API key hanya disimpan sebagai hash | Diuji otomatis | `ApiKey_hanya_hash_tersimpan_dan_token_diverifikasi_sebagai_akunnya` |
| Kunci Data Protection dipersistensi di PostgreSQL | Diuji otomatis | `Pg_DataProtection_enkripsi_dan_dekripsi_lintas_provider` |
| **Kunci Data Protection terenkripsi saat tersimpan** | **Celah** | Kunci tersimpan **tanpa enkripsi** di `docframe_data_protection_keys` (tidak ada `ProtectKeysWith*`). Siapa pun yang memegang dump database bisa memalsukan cookie sesi. **Perlakukan file backup sebagai rahasia.** |

### A03: Injection
| Kontrol | Status | Bukti |
|---|---|---|
| SQL injection: input diperlakukan sebagai teks | Diuji otomatis | `Pg_injeksi_sql_diperlakukan_sebagai_teks` |
| Filter dan sort hanya dari allowlist field | Diuji otomatis | `Filter_dan_sort_hanya_dari_allowlist` |
| Query dashboard: parameter dikirim native, bukan digabung string | Diuji otomatis | `Dashboard_parameter_dikirim_native_bukan_digabung` |
| Query dashboard hanya baca, tabel inti tertutup | Diuji otomatis | `Dashboard_hanya_baca_dan_tabel_inti_tertutup` |
| XSS pada UI | Ada di kode | Razor/Blazor meng-encode output secara default. Tidak ada sanitasi HTML khusus; jangan render `MarkupString` dari input pengguna. |

### A04: Insecure Design
| Kontrol | Status | Bukti |
|---|---|---|
| Transactional outbox, tidak diproses ganda | Diuji otomatis | `Pg_outbox_tidak_diproses_ganda_oleh_dua_worker` |
| Scheduled job tidak jalan ganda lintas instance | Diuji otomatis | `Pg_PostgresDistributedLock_mencegah_eksekusi_bersamaan_lintas_koneksi` |
| Cek link sebelum delete/cancel | Diuji otomatis | `LinkCheck_*`, `Pg_LinkCheck_*` |

### A05: Security Misconfiguration
| Kontrol | Status | Bukti |
|---|---|---|
| Header `nosniff`, `X-Frame-Options: SAMEORIGIN`, Referrer-Policy, Permissions-Policy, CSP | Diuji otomatis | `HttpHardening_SecurityHeadersMiddleware_menambahkan_semua_header_keamanan` |
| HSTS 365 hari | Ada di kode | `AddHsts` di `HttpHardening.cs` |
| Error 500 tidak membocorkan stack trace ke klien | Ada di kode | Exception handler mengembalikan problem+json generik; detail hanya di log server dan `docframe_error_logs` |
| **CSP cukup ketat** | **Celah** | `script-src` masih mengizinkan `'unsafe-inline'` dan `'unsafe-eval'`, jadi perlindungan XSS dari CSP lemah. Perlu nonce/hash bila memungkinkan dengan Blazor. |

### A06: Vulnerable and Outdated Components
| Kontrol | Status | Bukti |
|---|---|---|
| Tidak ada paket NuGet dengan advisory yang diketahui | Diperiksa manual (2 Nov 2026) | `dotnet list package --vulnerable` → 0 temuan di 5 project |
| `Framework.Core` tanpa paket pihak ketiga | Ada di kode | `Framework.Core.csproj` tidak memiliki `PackageReference` |
| Pemeriksaan otomatis di CI | Ada di kode | Langkah `dotnet list package --vulnerable --include-transitive` di `.github/workflows/ci.yml`; build gagal bila ada temuan |

### A07: Identification and Authentication Failures
| Kontrol | Status | Bukti |
|---|---|---|
| Lockout setelah 5 kali gagal (15 menit) | Diuji otomatis | `Login_password_hash_dan_lockout_setelah_5_kali_gagal` |
| Akun nonaktif ditolak, API key-nya ikut tidak berlaku | Diuji otomatis | `Login_akun_nonaktif_ditolak_dan_hilang_dari_direktori`, `ApiKey_dicabut_kedaluwarsa_ip_dan_akun_nonaktif_ditolak` |
| Perubahan role mengganti security stamp | Diuji otomatis | `Login_perubahan_role_mengganti_security_stamp_dan_klaim` |
| Perlindungan CSRF: API yang mengubah data wajib bearer token | Ada di kode | Cek di `DocApi.Run`; belum ada test HTTP khusus |
| MFA / SSO | **Belum** | Ada di roadmap P2 |

### A08: Software and Data Integrity Failures
| Kontrol | Status | Bukti |
|---|---|---|
| Optimistic concurrency (kolom `version`) | Diuji otomatis | `Konflik_versi_terdeteksi`, `Pg_update_bersamaan_dari_dua_instance` |
| Nomor seri unik saat insert paralel | Diuji otomatis | `Pg_nomor_seri_unik_saat_insert_paralel` |
| Nilai compute dari client tidak dipercaya | Diuji otomatis | `Nilai_compute_dari_client_tidak_dipercaya` |

### A09: Security Logging and Monitoring Failures
| Kontrol | Status | Bukti |
|---|---|---|
| Audit trail untuk operasi dokumen dan cetak | Diuji otomatis | `Cetak_menegakkan_permission_dan_tercatat_di_audit` |
| Error tak tertangani tercatat per tenant | Diuji otomatis | `ErrorLog_InMemoryStore_Lifecycle`, `Pg_ErrorLog_PostgresStore_Lifecycle` |
| Metrik dan trace OpenTelemetry | Ada di kode (sebagian) | Lihat [docs/observability.md](observability.md) |
| Alerting | **Belum** | Belum ada aturan alert; bergantung pada backend telemetri yang dipakai |

### A10: Server-Side Request Forgery (SSRF)
| Kontrol | Status | Bukti |
|---|---|---|
| Permukaan SSRF | Tidak relevan saat ini | Tidak ada pemanggilan HTTP keluar di `src/` (tidak ada `HttpClient`). **Wajib ditinjau ulang** saat fitur webhook/email (P1-2, P2) ditambahkan. |

---

## 2. Daftar Celah yang Harus Ditindaklanjuti

| Prioritas | Celah | Tindakan |
|---|---|---|
| Tinggi | Kunci Data Protection tidak terenkripsi saat tersimpan | Enkripsi kunci (sertifikat X.509 / KMS), atau minimal batasi akses ke tabel dan file backup |
| Sedang | CSP `script-src` memakai `'unsafe-inline'` dan `'unsafe-eval'` | Uji CSP berbasis nonce dengan Blazor; hapus `'unsafe-eval'` bila tidak dibutuhkan |
| Sedang | Belum ada DAST (OWASP ZAP baseline) | Jalankan `zap-baseline.py` terhadap staging dan tambahkan ke CI |
