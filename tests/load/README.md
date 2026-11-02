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
