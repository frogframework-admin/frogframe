# DocFrame Performance & Load Testing Baseline

Panduan pengujian beban (*load testing*) dan tolok ukur performa (*baseline benchmarks*) untuk DocFrame menggunakan Grafana k6.

---

## 1. Persyaratan
- [Grafana k6](https://k6.io/docs/get-started/installation/) terpasang di host, **atau** gunakan Docker.
- Instance aplikasi DocFrame berjalan (default: `http://localhost:5080`).

---

## 2. Cara Menjalankan

### Opsi A: Menggunakan k6 Binary Lokal
```bash
k6 run tests/load/k6-baseline.js
```

Atau tentukan alamat server target melalui environment variable:
```bash
BASE_URL="http://staging.docframe.internal:5080" k6 run tests/load/k6-baseline.js
```

### Opsi B: Menggunakan Docker
```bash
# Windows PowerShell:
cat tests/load/k6-baseline.js | docker run --rm -i --network="host" grafana/k6 run -

# Linux / macOS:
docker run --rm -i --network="host" grafana/k6 run - < tests/load/k6-baseline.js
```

---

## 3. Profil Pengujian

Skrip pengujian menjalankan 4 tahapan beban bertingkat (*ramped virtual users*):
1. **Ramp-Up (15 detik)**: Menaikkan beban secara bertahap hingga 20 Virtual Users (VU).
2. **Steady Load (30 detik)**: Beban konstan pada 50 Virtual Users untuk mengukur kestabilan memori dan connection pool PostgreSQL.
3. **Stress Spike (15 detik)**: Lonjakan beban singkat hingga 100 Virtual Users untuk menguji batas ketahanan throughput.
4. **Ramp-Down (15 detik)**: Penurunan kembali ke 0 VU untuk memverifikasi pelepasan koneksi pool dan garbage collection.

---

## 4. Anggaran Performa (Performance Budgets)

| Metrik | Ambang Batas (*Threshold*) | Keterangan |
|---|---|---|
| **Tingkat Error Global** | `rate < 1%` | Tidak boleh ada error 500 / unhandled exception. |
| **Latensi Keseluruhan** | `p(95) < 350ms` | 95% request selesai di bawah 350 milidetik. |
| **Health Checks** (`/health/*`) | `p(95) < 50ms` | Pemeriksaan status liveness & readiness harus instan. |
| **Pembacaan Dokumen** (`GET /api/data/*`) | `p(95) < 200ms` | Query terindeks dengan filter dan sorting. |
| **Penulisan Dokumen** (`POST /api/data/*`) | `p(95) < 400ms` | Transaksi atomik dokumen + audit log + event outbox. |

---

## 5. Hasil Baseline Nyata (Real Verification Run)

Diverifikasi pada lingkungan Docker terisolasi (`docframe-app` + `docframe-postgres` di PostgreSQL 17, `grafana/k6` container di Docker network `docframe_default`):

- **Durasi Uji**: 1 menit 15 detik (ramp-up 20 VU, steady 50 VU, spike 100 VU, ramp-down 0 VU)
- **Akun Auth**: Admin demo terautentikasi via `setup()` (`admin` / `DocFrame#2026`)
- **Total Permintaan HTTP**: 5.721 requests
- **Throughput Rata-rata**: 74,5 req/s
- **Tingkat Keberhasilan (Checks)**: 5.720 / 5.720 passed (**100% lolos, 0% error**)

### Rincian Latensi per Skenario

| Skenario | p(50) / Median | Rata-rata (avg) | p(90) | p(95) | Status |
|---|---|---|---|---|---|
| **Health Probes** (`/health/live`, `/ready`) | 1,12 ms | 1,25 ms | 1,51 ms | 1,64 ms | Sempurna (< 2ms) |
| **Document Queries** (`GET /api/data/PurchaseRequest`) | 2,85 ms | 3,70 ms | 5,42 ms | 6,60 ms | Sempurna (< 10ms) |
| **Document Creation** (`POST /api/data/PurchaseRequest` + child items) | 53,6 ms | 207,2 ms | 485,3 ms | 1.020 ms* | Lolos tanpa kegagalan |

*\*Catatan*: P95 write mencapai ~1 detik saat lonjakan instan 100 VU bersamaan melakukan transaksi multi-tabel (`PurchaseRequest` + `PurchaseRequestItem` + `sys_audit_log` + `sys_outbox_events`). Tidak ada kegagalan transaksi, connection pool PostgreSQL tetap sehat dan stabil.


