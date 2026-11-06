# DocFrame — prototipe RFC .NET Business Framework Berbasis Metadata

Prototipe ini mengimplementasikan fondasi RFC v2 ("Mendekati Frappe Framework"). Ada enam fondasinya: DocType, document service, permission, lifecycle/workflow, generic views/API, dan extension system. Ditambah audit, outbox/notifikasi, report, serta publish metadata berversi. Pilotnya **Pengajuan Pembelian** sesuai RFC §18.

Teknologi: **.NET 10**, ASP.NET Core, Blazor Server (interactive), dan **[Blazor Blueprint UI](https://blazorblueprintui.com) 4.1.0** (komponen gaya shadcn/ui untuk Blazor).

Penyimpanan bisa dipilih lewat konfigurasi: **in-memory** (default, untuk demo) atau **PostgreSQL** (permanen, siap pilot). Keduanya lulus rangkaian test yang sama.

## Status terkini (v19)

Bagian ini diperbarui setiap versi dan lebih akurat daripada daftar batasan di bawah, yang ditulis untuk versi awal.

**Sudah dikerjakan dan diuji** (in-memory 119 test lulus, PostgreSQL 152 test lulus; startup dari database kosong bersih):
- Impor Excel/CSV tahap 1, laporan Excel/Word dari template, API kunci API, method kustom, dan API dari query tersimpan.
- Role master sebagai satu sumber: publish menolak role yang tidak ada di master; rename atau hapus role yang masih dipakai ditolak; akun pengguna divalidasi.
- Master modul wajib untuk setiap DocType (bukan child table). Kode modul huruf besar, kekal setelah dibuat, dan unik per tenant. Sidebar menampilkan DocType per modul aktif.
- Kode departemen huruf besar (`DIREKSI`, `MARKETING`, dst.). Nama role tidak diubah.
- Pencarian global menampilkan nama modul.
- Audit metadata saat startup: metadata yang tidak cocok dengan master tenant dilaporkan ke log (`Metadata:AuditTenants`). Tidak mengubah metadata.
- Pembersihan file yang tidak pernah ditautkan ke dokumen (`Files:OrphanHours`, default 24 jam).

**Belum dikerjakan** (prasyarat prototipe akuntansi):
- Filter Link statis dari metadata (mis. akun daun saja untuk jurnal).
- Single DocType untuk pengaturan per tenant.
- Hook BeforeDelete dan BeforeCancel.
- Pemisahan peran database owner dan app, untuk append-only.
- Impor mode Perbarui dan Tambah-atau-perbarui.

**Catatan build di sandbox pengembangan**: paket OpenTelemetry dan Blazor Blueprint diganti stub sementara karena NuGet tidak bisa diakses dari sandbox. Di mesin pengembang dengan akses NuGet, restore biasa seharusnya cukup, tetapi perlu dikonfirmasi sekali.

---

## Menjalankan

Butuh .NET 10 SDK (`global.json` meminta 10.0.100 atau lebih baru). Paket diambil dari nuget.org.

```bash
dotnet run --project src/Apps.Sample          # buka http://localhost:5080
dotnet run --project tests/Framework.Tests    # 17 skenario uji
```

Data contoh dibuat ulang setiap kali aplikasi start. Seeding berjalan lewat `DocumentService`, jadi seluruh pipeline validasi dan workflow ikut teruji saat startup.

### Build terhadap source library (opsional)

Secara default `Framework.Web` memakai paket NuGet `BlazorBlueprint.Components` (versi di properti `BlazorBlueprintVersion`). Untuk men-debug atau mencoba versi pengembangan library:

```bash
git clone https://github.com/blazorblueprintui/ui.git ../bb-ui
dotnet build -p:BlazorBlueprintSource=../bb-ui/src
```

Hal yang sama berlaku untuk Npgsql (`Framework.Persistence.PostgreSql`): default memakai paket NuGet `Npgsql` 10.0.3, atau `-p:NpgsqlSource=../npgsql/src` untuk build dari source.

## UI dengan Blazor Blueprint

| Bagian | Komponen |
|---|---|
| Kerangka | `BbSidebarProvider`/`BbSidebar`/`BbSidebarInset`, ikon Lucide, `BbSelect` (pemilih pengguna demo), `BbDarkModeToggle` |
| Daftar | `BbInput` (cari, debounce), `BbSelect` & `BbDatePicker` (filter), `BbBadge` (status), `BbPagination`, `BbEmpty`, `BbSkeleton` |
| Form dinamis | `BbTabs`, `BbCard`, `BbField`/`BbFieldLabel`/`BbFieldError`, `BbInput`, `BbTextarea`, `BbSelect`, `BbCheckbox`, `BbDatePicker`, `BbCombobox` (field Link, pencarian sisi server) |
| Aksi | `BbButton`; `DialogService.PromptAsync` untuk komentar wajib (Tolak/Batalkan), `ConfirmAsync` untuk hapus; `ToastService` untuk konfirmasi |
| Riwayat | `BbTimeline` (mode komposisi) |
| Lain-lain | `BbAlert` (termasuk konflik versi dengan tombol muat ulang), `BbBreadcrumb`, `BbAvatar` |

Catatan penerapan:

- **Tema** ada di `src/Framework.Web/wwwroot/theme.css`. Isinya variabel shadcn/ui (OKLCH) untuk mode terang dan gelap, dimuat sebelum `blazorblueprint.css` sesuai panduan tema Blazor Blueprint. Tema bisa diganti dengan ekspor dari tweakcn.com.
- **Tata letak** (`desk.css`) memakai CSS biasa di atas variabel tema, **tanpa build Tailwind** di sisi aplikasi. Stylesheet ini tidak berada di cascade layer, sehingga menang atas utilitas `bb:` milik library tanpa perlu `!important` (kecuali judul kartu).
- **Bahasa:** teks bawaan komponen diterjemahkan lewat `DefaultBbLocalizer` (`BbIndonesian.cs`). Budaya aplikasi `id-ID`, sehingga nama bulan di date picker ikut Indonesia. Penyimpanan dan API tetap memakai format invarian (`yyyy-MM-dd`, titik desimal).
- **Komentar wajib:** divalidasi di aplikasi, bukan lewat `PromptDialogOptions.Required`. `BbInput` meng-commit nilai saat blur, sehingga dengan `Required` tombol konfirmasi tetap nonaktif selama pengguna masih mengetik. Server tetap menolak aksi tanpa komentar.
- **Input angka:** memakai `BbInput` biasa, bukan `BbNumericInput`/`BbCurrencyInput`. Komponen angka Blazor Blueprint tidak mendukung nilai kosong (`null`), padahal field opsional perlu bisa kosong. Pemisah ribuan format Indonesia (`2.750.000`) diterima.
- **Cap stempel status** di kepala dokumen adalah komponen sendiri (`StatusStamp`); di daftar memakai `BbBadge`.

## Login & pengguna (ASP.NET Core Identity)

Login memakai **ASP.NET Core Identity** dengan penyimpanan akun DocFrame (in-memory atau PostgreSQL, tanpa EF Core): cookie untuk browser, bearer token untuk API. Tenant, departemen, karyawan, dan role disimpan di akun, diterbitkan sebagai **klaim** saat login, lalu dipetakan ke `UserContext` (tidak berubah) oleh `ClaimsUserMapper` — satu-satunya tempat pemetaan.

| Klaim | Isi | Di `UserContext` |
|---|---|---|
| `nameidentifier` | id akun (tetap) | `Id` (dipakai sebagai owner dokumen) |
| `docframe:tenant` | tenant | `TenantId` |
| `docframe:full_name` | nama lengkap | `FullName` |
| `role` (bisa lebih dari satu) | role | `Roles` |
| `docframe:department` | departemen | `Attributes["department"]` (row scope `match:department=department`) |
| `docframe:employee` | dokumen Employee | `Attributes["employee"]` (default `$employee`) |

Klaim tanpa tenant ditolak (gagal tertutup), walau mengaku System Manager.

**Keamanan**: password di-hash (PBKDF2, format Identity v3), kebijakan minimal 10 karakter dengan huruf besar/kecil dan angka, akun terkunci 15 menit setelah 5 kali gagal, akun nonaktif tidak bisa login, pesan login tidak membedakan "username tidak ada" dan "password salah", `returnUrl` hanya lokal, logout lewat POST dengan antiforgery. Perubahan role/departemen/status/password mengganti *security stamp*: sesi Blazor yang sedang terbuka dicek ulang setiap menit dan memuat klaim baru (atau keluar bila dinonaktifkan).

**Halaman**:
- `/account/login`, `/account/logout`, `/account/password` (ganti password; klik nama di header).
- `/admin/users` (System Manager): tambah pengguna dengan password sementara, atur role (diambil dari permission & workflow metadata), departemen, karyawan, status aktif; reset password; buka kunci. Tenant mengikuti admin dan tidak bisa diubah dari UI; admin tidak bisa menonaktifkan diri sendiri atau mencabut System Manager dari dirinya.

**Akun awal**:
- *Development*: `Demo:SeedData = true` membuat 8 akun demo + data contoh.
- *Pilot/Production*: data demo mati. Isi `Bootstrap:AdminUserName`, `Bootstrap:AdminPassword`, `Bootstrap:TenantId` (sebaiknya lewat environment variable) — satu akun System Manager dibuat **hanya bila belum ada akun sama sekali**. Setelah login pertama, ganti password dan hapus `Bootstrap:AdminPassword` dari konfigurasi.

```bash
Persistence__Provider=PostgreSql \
Bootstrap__AdminUserName=superadmin Bootstrap__AdminPassword='GantiSetelahLogin2026' Bootstrap__TenantId=pt-contoh \
dotnet run --project src/Apps.Sample --environment Production
```

## Editor DocType visual

Buka **Metadata DocType → pilih DocType → Buka editor visual** (`/admin/metadata/{DocType}/editor`, khusus System Manager).

- **Desain**: seret tipe field dari palet ke kolom mana pun, seret kartu field untuk memindah (antar kolom, section, dan tab), atau pakai tombol ↑ ↓ (alternatif keyboard). Tab dan section bisa ditambah, diberi judul, digeser, dan diatur 1–3 kolom. Klik field untuk mengubah properti: label, nama, tipe, opsi, relasi, wajib/tampil/hanya-baca bila, rumus, default, ambil dari Link, batas angka/panjang/pola, kolom daftar, dan level permission.
- **Validasi langsung**: setiap perubahan dikompilasi bersama metadata aktif lain memakai compiler yang sama dengan saat startup. Error ekspresi tampil di bawah input terkait dan kartu field ditandai merah.
- **Pratinjau**: form dirender dari draft memakai komponen form aplikasi. Kondisi tampil/wajib dan rumus berjalan langsung; **Uji validasi** menjalankan validasi server terhadap data uji (tidak disimpan).
- **Akses & alur**: permission, aturan dokumen, API, dan workflow (lihat bagian di bawah).
- **JSON**: definisi lengkap draft; untuk hal yang belum ada di editor visual (aturan lintas-field, format cetak). Bisa diterapkan kembali ke desain.
- **Periksa perubahan → Publikasikan**: memakai diff dan klasifikasi risiko yang sama (aman / perlu transformasi / berisiko); perubahan berisiko wajib dikonfirmasi. Menghapus atau mengganti nama field lama diberi peringatan.

Editor tidak mengubah susunan field bila layout tidak diubah, sehingga diff hanya berisi perubahan yang benar-benar dibuat.

## Membuat DocType, akses, dan workflow dari UI

Semua bisa dilakukan System Manager tanpa menyentuh JSON. Setiap perubahan divalidasi compiler, diperiksa dengan diff + klasifikasi risiko, lalu dipublikasikan sebagai versi immutable (permanen di mode PostgreSQL).

**Buat DocType** (`Metadata DocType → Buat DocType`, `/admin/metadata/new`): isi judul; nama teknis (PascalCase) dan pola nomor (mis. `PC-{YYYY}-{#####}`) terisi otomatis. Pilih jenis (dokumen atau baris child table) dan template:

| Template | Isi |
|---|---|
| Dengan persetujuan | field Judul/Tanggal/Keterangan, permission (karyawan: miliknya sendiri; Manager: baca semua), workflow Draft → Diajukan → Disetujui / Ditolak, amend aktif |
| Sederhana | field dasar + permission, tanpa workflow |
| Kosong | satu field Judul; hanya System Manager yang punya akses |

DocType baru langsung muncul di menu (dikelompokkan per modul) dan terbuka di editor visual.

**Akses & alur** (tab di editor DocType): aturan permission per role — aksi (baca, buat, ubah, hapus, submit, batal, amend, laporan, ekspor), cakupan data (semua / milik sendiri / sama dengan atribut pengguna seperti departemen), dan level field; aturan dokumen (submit, batal, amend, hapus, audit); serta akses API. Perubahan permission diklasifikasikan *berisiko* dan meminta konfirmasi saat publish.

**Editor workflow** (`/admin/workflows/{Nama}/editor`, atau "Buat workflow" dari DocType):
- **State**: nama, status dokumen (draft / terkunci / dibatalkan), warna, state awal. Mengganti nama state otomatis memperbarui transisi yang memakainya.
- **Transisi**: dari → ke, nama aksi (label tombol), role yang boleh, kondisi (ekspresi yang divalidasi langsung; variabel: field DocType, `owner`, `user.id`, `user.department`), dan wajib komentar.
- **Diagram** diperbarui langsung (alur mengalir kiri ke kanan; klik panah untuk mengedit transisinya).
- **Publish**: diff khusus workflow — menghapus state atau mengubah role/kondisi/tujuan transisi = *berisiko*; menghapus transisi atau mengganti state awal = *perlu transformasi*; menambah state/transisi = *aman*. Workflow baru bisa langsung **dipasang ke DocType** dalam satu langkah (DocType ikut naik versi). Dokumen draft yang dibuat sebelum workflow dipasang otomatis dianggap berada di state awal.

Batasan desain (sesuai RFC): transisi tidak boleh menurunkan status dokumen (mis. dari terkunci kembali ke draft). Untuk revisi, tolak dokumen lalu gunakan **Amend**.

## Struktur organisasi & gambar

**Departemen** (menu HR → Departemen) adalah DocType pohon: setiap departemen punya *induk departemen* (lookup), kepala departemen, dan **kode** yang sekaligus menjadi nomor dokumen (`naming: field:code`). Kode tidak bisa diubah setelah disimpan, sehingga nilai departemen di karyawan, akun pengguna (klaim `docframe:department`), dan cakupan data `match:department=department` tetap konsisten.

**Karyawan** punya field **Atasan** (`atasan_id`, Link ke Employee dengan dialog lookup; nama atasan terisi otomatis), Jabatan, Foto, dan Departemen (Link ke Departemen).

**Pencegahan siklus (generik)**: setiap field Link yang menunjuk DocType-nya sendiri — induk departemen, atasan, atau DocType pohon lain yang Anda buat — tidak boleh menunjuk dirinya sendiri atau membentuk siklus (mis. A atasan B, B atasan A). Pesan error menyebut rantainya, mis. `IT → Infrastruktur → IT`.

**Struktur Organisasi** (menu HR → Struktur Organisasi, `/hr/struktur-organisasi`): bagan per departemen (kepala, foto, jumlah karyawan aktif) dan bagan atasan–bawahan (foto, jabatan, departemen). Cabang bisa dibuka/ditutup; kotak menuju data departemen/karyawan; departemen dan posisi pengguna yang login disorot. Bagannya komponen generik `OrgTree` yang bisa dipakai untuk DocType pohon lain. Item menu ditambahkan lewat `AddDeskMenuItem(new DeskMenuItem("HR", "Struktur Organisasi", "/hr/struktur-organisasi", "network", RequiresReadOf: "Employee"))`.

**Field gambar** (tipe `Image`; dipakai di Foto karyawan dan Gambar produk):
- Unggah PNG, JPEG, WebP, atau GIF, maks. 2 MB. Format **dikenali dari isi file** (bukan nama/ekstensi), SVG tidak diizinkan (bisa berisi skrip).
- Disimpan di database pada mode PostgreSQL (`docframe_files`, ikut backup dan tersedia di semua instance).
- Akses `/files/{id}`: gambar yang sudah tertaut ke dokumen hanya untuk yang boleh membaca dokumen itu (dan dokumen masih memakai gambar tersebut); sebelum tertaut hanya untuk pengunggah; tenant lain dan tanpa login ditolak. Respons memakai `nosniff` dan CSP ketat.
- Id gambar di payload divalidasi: harus file milik tenant yang diunggah pengguna sendiri atau sudah milik dokumen itu — id file orang lain ditolak.
- Thumbnail tampil di daftar dan bagan organisasi.

## Dashboard (widget dari query SQL)

Menu **Dashboard** menampilkan widget yang di-pin; System Manager membuat widget lewat wizard 4 langkah (`Dashboard → Widget baru`):

1. **Query** — satu perintah `SELECT`/`WITH` ke view di skema `reporting`. Panel samping menampilkan view & kolom yang tersedia (klik untuk menyisipkan). Parameter ditulis `@NamaParameter`; teks di dalam string literal dan komentar tidak dianggap parameter.
2. **Parameter & pratinjau** — tipe tiap parameter (Teks, Angka, Tanggal, Boolean; ditebak dari nama), nilai uji, dan **binding** saat tampil di dashboard: nilai default, filter dashboard (tanggal mulai/akhir, departemen), atau konteks pengguna (departemen/ID pengguna yang melihat). Hasil pratinjau tampil sebagai data grid (maks. 100 baris).
3. **Visual** — thumbnail Batang, Garis, Pie, Doughnut, Angka tunggal, Tabel data; pemetaan sumbu X (kategori) dan Y (nilai, wajib numerik — kolom non-angka tidak bisa dipilih). Pratinjau langsung.
4. **Simpan** — nama, deskripsi, role yang boleh melihat (kosong = semua), tempel ke dashboard.

Di dashboard: filter global di atas, kartu bisa diperlebar (1–3 kolom), digeser, diedit, atau dilepas. Grafik berupa SVG (tanpa JavaScript) yang mengikuti tema terang/gelap.

**View pelaporan**: `reporting.<doctype>` (mis. `reporting.purchase_request`) dan `reporting.<doctype>_<child_table>` (mis. `reporting.purchase_request_items`) dibuat otomatis dari metadata dan diperbarui setiap kali metadata dipublikasikan. Kolom standar `name, owner, docstatus, workflow_state, created, modified` + field (angka → `numeric`, tanggal → `date`, centang → `boolean`). Field berlevel izin > 0 dan field gambar **tidak** diekspos.

Filter opsional: nilai parameter kosong dikirim sebagai `NULL`, sehingga pola berikut bekerja:

```sql
SELECT department, COUNT(*) AS jumlah
FROM reporting.purchase_request
WHERE request_date BETWEEN @StartDate AND @EndDate
  AND (@Dept IS NULL OR department = @Dept)
GROUP BY department
```

**Konfigurasi tersimpan sebagai JSON** (tabel `docframe_dashboard_widgets.config`, layout di `docframe_dashboards.layout`):

```json
{
  "schemaVersion": 1,
  "sql": "SELECT department, COUNT(*) AS jumlah FROM reporting.purchase_request WHERE request_date >= @StartDate GROUP BY department",
  "parameters": [{ "name": "StartDate", "label": "Dari", "type": "date", "defaultValue": "2026-01-01", "binding": "global:start" }],
  "visual": { "type": "bar", "xColumn": "department", "yColumns": ["jumlah"] },
  "roles": ["Finance", "Manager"]
}
```

**Keamanan eksekusi query** (berlapis):

| Lapisan | Perlindungan |
|---|---|
| User database read-only | Koneksi terpisah (`ConnectionStrings:DocFrameReadOnly`). Sebelum menjalankan query, aplikasi **memverifikasi** user tersebut: bukan superuser, tanpa hak INSERT/UPDATE/DELETE, tidak bisa membaca tabel dokumen/akun/sesi secara langsung, tidak bisa membuat objek. Bila salah satu gagal, dashboard menolak berjalan. |
| Isolasi tenant | View memfilter tenant dari tabel sesi yang diisi aplikasi lewat koneksi utama, dikunci per process id koneksi read-only. User read-only tidak punya hak atas tabel itu, sehingga `set_config`/`SET` dari dalam query tidak bisa mengganti tenant. |
| Transaksi | `READ ONLY` + `statement_timeout` (default 5 detik) + selalu di-rollback. |
| Pembungkus | `SELECT * FROM (query) AS __q LIMIT n` (pratinjau 100, widget 1000). |
| Parameter | `@Nama` diganti placeholder `$1…` di luar string/komentar; nilai dikirim **native** ke Npgsql dengan tipe yang dideklarasikan, tidak pernah digabung ke teks SQL. |
| Validasi teks | Satu statement saja; harus `SELECT`/`WITH`; dollar-quoting ditolak. |

Hak akses: merancang widget hanya System Manager; widget bisa dibatasi per role. **Widget menampilkan data seluruh tenant** — aturan cakupan "milik sendiri"/departemen dari permission DocType tidak berlaku di SQL. Gunakan binding `user:department` atau batasi role untuk data sensitif.

**Mengaktifkan**:

```bash
# 1) sekali, sebagai superuser/DBA (docker compose menjalankannya otomatis saat volume baru)
psql -U postgres -d docframe -f deploy/sql/reporting-readonly-user.sql
# 2) jalankan aplikasi dengan dua connection string
Persistence__Provider=PostgreSql \
ConnectionStrings__DocFrameReadOnly="Host=localhost;Database=docframe;Username=docframe_ro;Password=..." \
dotnet run --project src/Apps.Sample
```

Tanpa PostgreSQL atau tanpa connection string read-only, halaman Dashboard menampilkan pemberitahuan dan fitur lain tetap berjalan.

**Bila menu Dashboard terbuka tetapi grafik tidak muncul**, cocokkan dengan yang tampil:

| Yang tampil | Penyebab | Perbaikan |
|---|---|---|
| Kotak "Dashboard SQL belum aktif" | Mode in-memory, atau `ConnectionStrings:DocFrameReadOnly` kosong | Jalankan dengan `Persistence__Provider=PostgreSql` dan isi connection string read-only |
| Pesan merah "Login dashboard gagal untuk user 'docframe_ro'…" di kartu | Role read-only belum dibuat atau password beda. Docker compose hanya menjalankan `reporting-readonly-user.sql` saat **volume database baru** | Jalankan `docker compose exec -T postgres psql -U docframe -d docframe < deploy/sql/reporting-readonly-user.sql`, samakan password, restart aplikasi |
| "Belum ada widget di dashboard" | Widget contoh dilewati karena koneksi read-only gagal saat startup (lihat peringatan "Dashboard SQL tidak bisa dipakai" di log), atau `Demo:SeedData` mati | Perbaiki koneksi read-only lalu restart — widget contoh dibuat otomatis selama tenant belum punya widget; atau buat lewat "Widget baru" |

Saat startup aplikasi memeriksa koneksi read-only dan menulis peringatan di log bila bermasalah; aplikasi dan fitur lain tetap berjalan.

## Impor Excel/CSV ke DocType

Tombol **Impor** di halaman daftar setiap DocType (untuk pengguna yang boleh membuat dokumennya; nonaktifkan per DocType dengan `"import": { "enabled": false }`). **Impor tidak punya aturan sendiri**: setiap baris diproses lewat `DocumentService` persis seperti mengisi form — permission, cakupan baris, level field, validasi & aturan wajib, hook, penomoran, workflow awal (draft), audit, dan outbox.

Wizard 4 langkah:

1. **File** — unduh **template Excel** yang dibuat dari metadata: hanya kolom yang boleh diisi pengguna itu (field turunan & berlevel izin lebih tinggi tidak ada), kolom wajib bertanda `*`, dropdown untuk Select/Centang, satu sheet per child table, dan sheet **Petunjuk** (label ↔ nama field, nilai yang diterima). Atau unggah `.xlsx`/`.csv` sendiri. Riwayat impor tampil di sini.
2. **Pencocokan kolom** — otomatis menurut label atau nama field (tanpa beda huruf besar/kecil), bisa diubah; kolom wajib yang belum terpetakan menghentikan proses.
3. **Simulasi** — semua baris dijalankan lewat seluruh aturan **tanpa disimpan** (nomor urut tidak terpakai); error per baris dengan **nomor baris Excel**. Pilih: *lewati baris gagal* (default) atau *semua atau tidak sama sekali* (satu commit atomik).
4. **Hasil** — daftar dokumen yang dibuat, **file baris gagal** (nilai asli + kolom "Alasan") untuk diperbaiki dan diimpor ulang. Audit setiap dokumen: "Dibuat lewat impor #imp_…".

**Nilai yang diterima**: tanggal (sel tanggal Excel, `2026-10-30`, `30/10/2026`, `30 Oktober 2026`), angka (sel angka atau `1.500.000,50` format Indonesia, `Rp`), Select (nilai, tanpa beda huruf besar/kecil), Centang (Ya/Tidak, 1/0, true/false), Link (nomor dokumen **atau** judulnya — judul ganda = error, tidak ditebak). Sel rumus memakai hasil hitung yang tersimpan.

**Child table**: satu sheet per tabel; kolom **"Kunci impor"** (isi bebas: 1, 2, 3) menghubungkan baris tabel ke baris dokumen — aman diurutkan/difilter di Excel. Baris tabel dengan kunci yang tidak ada diperingatkan.

**Batasan & keamanan**: `Import:MaxRows` (10.000), `Import:MaxFileMb` (10); pengaman file yang sama dengan template laporan (makro ditolak, zip bomb, tanpa DTD); `.xls` lama ditolak dengan petunjuk simpan ulang.

## API integrasi: kunci API & method kustom

Setiap DocType yang API-nya diaktifkan (`"api": { "enabled": true, "operations": [...] }`, atau tab **Akses & alur** di editor) otomatis punya REST API di `/api/data/{DocType}` — padanan `/api/resource` di Frappe. **API tidak punya aturan sendiri**: setiap permintaan melewati `DocumentService` yang sama dengan form (permission role, cakupan baris, level field, validasi, workflow, cek versi, audit).

**Kunci API** (menu **Kunci API**, System Manager) — untuk sistem lain yang berjalan terus-menerus:

```bash
curl -H "Authorization: ApiKey dfk_k1a2b3….<secret>" http://localhost:5080/api/data/PurchaseRequest
```

- Kunci melekat pada sebuah **akun**: role, tenant, dan departemen akun itulah yang berlaku. Disarankan **akun integrasi** khusus — dibuat langsung dari halaman Kunci API, tanpa password (tidak bisa login lewat layar).
- **Cakupan opsional**: `doc:{DocType}:{list|read|create|update|delete|action}`, `method:{nama}`, `report:{id}`, dengan `*` per segmen. Kosong = semua yang diizinkan role akun. Endpoint yang tidak mendeklarasikan cakupan (admin metadata, laporan bawaan) **menolak kunci API** — gagal tertutup.
- Secret 256-bit ditampilkan **sekali**; yang disimpan hanya hash SHA-256 (perbandingan waktu-konstan). Kedaluwarsa, daftar IP/CIDR, cabut seketika, catatan terakhir dipakai (waktu + IP). Akun dinonaktifkan → kuncinya ikut tidak berlaku.
- Kunci API boleh dipakai untuk permintaan yang mengubah data (seperti bearer token); cookie browser tetap tidak.

**Method kustom** — padanan `@frappe.whitelist()`. Logika C# yang tidak cocok sebagai CRUD satu dokumen:

```csharp
public sealed class SisaAnggaranMethod : IApiMethod
{
    public string Name => "sisa-anggaran";
    public IReadOnlyList<string> Roles => ["Finance", "Manager"];
    public IReadOnlyList<ApiMethodParam> Parameters => [new("departemen", ArgTypes.Text, Required: true), new("tahun", ArgTypes.Number)];
    public async Task<object?> InvokeAsync(ApiMethodContext ctx, CancellationToken ct) { … pakai DocumentService … }
}
builder.Services.AddApiMethod<SisaAnggaranMethod>();
```

```bash
curl -X POST -H "Authorization: ApiKey …" -H "Content-Type: application/json" \
     -d '{"departemen":"IT","tahun":2026}' http://localhost:5080/api/method/sisa-anggaran
# → { "data": { "departemen": "IT", "anggaran": 500000000, "terpakai": 129000000, "sisa": 371000000, … } }
curl -H "Authorization: ApiKey …" http://localhost:5080/api/method      # method yang boleh dipanggil
```

Argumen divalidasi ketat sebelum method berjalan: hanya parameter yang dideklarasikan (argumen asing ditolak — mencegah *mass assignment*), wajib, dan tipe (`text`, `number`, `date` yyyy-MM-dd, `boolean`, `list`). Method berjalan sebagai pemanggil; gunakan `DocumentService` agar hak akses tetap berlaku. `ApiMethodException` → 400 dengan pesan; kesalahan tak terduga → 500 generik tanpa detail internal (detail di log server). Contoh di aplikasi: `sisa-anggaran` (baca) dan `ajukan-massal` (mengajukan beberapa draft lewat workflow, hasil per dokumen).

**API dari query tersimpan** (menu **API Query**, System Manager) — padanan Server Script bertipe API di Frappe, tetapi **tanpa kode yang dieksekusi**: query SQL read-only yang sama dengan dashboard & laporan diterbitkan sebagai endpoint.

```bash
curl -H "Authorization: ApiKey …" "http://localhost:5080/api/query/pengajuan-per-departemen?Dept=Marketing"
# → { "data": [ { "nomor": "PR-2026-00004", … } ], "columns": [...], "count": 2, "truncated": false }
curl -H "Authorization: ApiKey …" http://localhost:5080/api/query       # API yang boleh dipanggil + parameternya
```

- **Satu jalur, dua cara**: wizard API Query (query → parameter & pratinjau → terbitkan), atau centang **"Terbitkan juga sebagai API"** saat menyimpan widget dashboard / template laporan — query & parameter yang sama diterbitkan dan ikut diperbarui setiap kali sumbernya disimpan; centang dilepas = API dihapus.
- **Opsi keluaran**: daftar baris JSON, satu objek (KPI), atau CSV (UTF-8 + BOM; nilai berawalan `= + - @` diberi apostrof — pertahanan formula injection). Batas baris (maks. 10.000, `truncated: true` bila terpotong), cache 0–3600 detik (`X-Cache: HIT/MISS`; versi baru membatalkan cache), aktif/nonaktif (nonaktif → 404), role pemanggil.
- **Parameter**: "dari pemanggil" (query string; default bila tidak dikirim) atau "dari akun" (departemen / ID pengguna — diisi otomatis dan **tidak bisa ditimpa**: kunci manager IT tidak bisa meminta data Marketing lewat URL). Parameter yang tidak dideklarasikan ditolak (422).
- Kunci API: cakupan `query:{slug}` atau `query:*`.

**Batas laju**: `Api:RateLimitPerMinute` (default 300) per kunci API / pengguna / IP untuk semua `/api`; melebihi → 429 + `Retry-After`.

## Laporan Excel/Word dari template

Menu **Laporan Excel/Word** (`/report-templates`). Alurnya sama dengan pembuat widget dashboard, tetapi hasilnya file yang mengikuti template milik Anda. Memakai eksekutor read-only dashboard yang sama (tenant terisolasi, `READ ONLY`, parameter native) — butuh PostgreSQL + `ConnectionStrings:DocFrameReadOnly`.

**Wizard (System Manager)**: 1) query detail + query header opsional (satu baris) → 2) parameter & pratinjau → 3) pilih Excel/Word, **unduh template contoh** berisi semua tag, edit tampilannya, unggah; setiap tag diperiksa terhadap parameter, kolom query, dan tag sistem; "Coba buat file" dengan data pratinjau → 4) nama, pola nama file (boleh memakai tag), role yang boleh membuat.

**Membuat laporan (pengguna)**: buka laporan → isi parameter → **Buat laporan** → unduh. Tautan unduhan berlaku 10 menit, sekali pakai, hanya untuk pengguna yang membuatnya. Klien API: `POST /api/report-templates/{id}/generate` dengan bearer token dan `{ "parameters": { "Dept": "IT" } }`.

**Tag**

| Tag | Isi |
|---|---|
| `{{Nama}}` | Data tunggal: parameter → kolom query header → kolom baris pertama query detail → tag sistem |
| `{{Today}}` `{{Now}}` `{{User}}` `{{UserId}}` `{{Tenant}}` `{{RowCount}}` | Tag sistem |
| `{{Sum:Kolom}}` `{{Avg:Kolom}}` `{{Min:Kolom}}` `{{Max:Kolom}}` `{{Count}}` | Agregat dari query detail |
| `[[Kolom]]` | **Baris diulang** per baris query detail: baris Excel, baris tabel Word, atau paragraf Word (mis. daftar berpoin) |
| `[[No]]` | Nomor urut 1, 2, 3, … |
| `…|format` | Format .NET (budaya id-ID), mis. `{{Tanggal|dd MMMM yyyy}}`, `[[Gaji|N0]]` |

**Excel**: sel yang hanya berisi satu tag ditulis **bertipe** (angka tetap angka, tanggal jadi tanggal Excel), sehingga rumus dan format sel template tetap bekerja. Baris template disalin bersama gayanya; rumus di baris template disalin seperti *fill-down*; rumus di bawah tabel ikut bergeser dan **rentang yang memuat baris template melebar** (`=SUM(D6:D6)` → `=SUM(D6:D105)`); sel gabungan, area cetak, conditional formatting, validasi data, hyperlink, autofilter, dan Excel Table ikut menyesuaikan; shared formula diurai; Excel menghitung ulang saat file dibuka. **Word**: tag yang terpecah ke beberapa *run* (hal biasa akibat koreksi ejaan Word) tetap dikenali; baris tabel disalin bersama border/font/format-nya; header & footer ikut diisi.

**Tanpa library pihak ketiga**: file `.xlsx`/`.docx` adalah ZIP berisi XML dan diproses langsung dengan `System.IO.Compression` + `System.Xml.Linq` — tanpa lisensi komersial (EPPlus) dan tanpa dependensi tambahan.

**Keamanan**: template maks. 5 MB; template bermakro (VBA) ditolak; batas ukuran setelah diekstrak (pertahanan zip bomb); XML diparse tanpa DTD (pertahanan XXE); nilai data selalu ditulis sebagai teks/angka, **nilai yang diawali `=` tidak pernah menjadi rumus**. Laporan yang melebihi batas baris (`Reports:MaxRowsExcel` 50.000, `Reports:MaxRowsWord` 5.000) **ditolak**, tidak dipotong diam-diam. Batas waktu query laporan `Reports:StatementTimeoutSeconds` (30). Laporan berisi data seluruh tenant — batasi role untuk data sensitif.

Batasan yang diketahui: tag di dalam rumus Excel tidak diproses; referensi rumus ke sheet *lain* yang ikut melebar tidak disesuaikan; satu query detail per template (beberapa tabel dalam satu template memakai data yang sama).

## Performa & fondasi lapisan analitik

**Indeks otomatis dari metadata** (`DocumentIndexSync`, berjalan saat startup dan setiap metadata dipublikasikan):

| Sumber di metadata | Indeks yang dibuat (parsial per DocType) |
|---|---|
| `listView.defaultSort` (field data) | B-tree dengan arah urut persis sama dengan query + `name` |
| `listView.filters` (field data) | B-tree `(tenant_id, ekspresi field)` |
| `listView.searchFields` (field teks) | GIN trigram (`pg_trgm`) untuk `ILIKE '%teks%'` |

Indeks bernama `ix_df_…` (memuat hash definisi), dibangun `CONCURRENTLY` (tabel tetap bisa ditulis), dijaga advisory lock (satu instance yang membangun), dan yang tidak lagi dibutuhkan metadata dibuang. Ekspresi indeks identik dengan ekspresi query (`DocumentIndexSync.Expression` dipakai keduanya) — diuji dengan memeriksa bahwa planner memakainya sebagai *Index Cond*.

**Perbaikan urutan daftar**: halaman daftar mengurutkan `modified DESC, name`. Indeks bawaan kini `(tenant_id, doctype, modified DESC, name)` dan kolom standar `NOT NULL` tidak lagi diberi klausa `NULLS` — sebelumnya PostgreSQL mengurutkan seluruh baris DocType sebelum mengambil satu halaman.

**Hitungan total dibatasi** `10.000+` (`QueryLimits.CountCap`): menghitung jutaan baris persis jauh lebih mahal dari mengambil satu halaman; di atas batas, daftar menampilkan "10.000+".

**Benchmark** (`dotnet run --project tests/Framework.Tests -- --bench 1000000`, PostgreSQL 16, **1 CPU**, 3,9 GB RAM; data sintetis menyerupai pengajuan pembelian):

| Query (median) | 200 rb · kondisi awal | 1 jt · tanpa indeks metadata | 1 jt · dengan indeks metadata |
|---|---|---|---|
| Daftar default | 426 ms | 2,6 ms | 2,3 ms |
| Halaman dalam (lewati 5.000) | 507 ms | 8,7 ms | 11,0 ms |
| Filter prioritas | 212 ms | 24,2 ms | 3,8 ms |
| Cari nama pemohon | 570 ms | 1.100 ms | 61,5 ms |
| Filter departemen + total, urut total | 196 ms | 548 ms | 330 ms |
| Agregasi dashboard (view, 1 tahun) | 523 ms | ±0,8–1,7 dtk | ±0,8–1,7 dtk |

Catatan jujur: kolom "kondisi awal" diukur pada 200 ribu dokumen sebelum perbaikan urutan; dua kolom 1 juta sudah memuat perbaikan urutan. Urut pada field yang tidak ada di `defaultSort` (mis. total) tetap butuh sort. Filter rentang tanggal pada data sintetis ini ±0,5 dtk karena tanggal dibuat berlawanan arah dengan `modified` (artefak data uji). **Agregasi dashboard lewat view tidak bisa dipercepat dengan indeks** (ekstraksi JSONB per baris; ±1 dtk per juta baris dan bervariasi karena sort) — inilah alasan tabel proyeksi analitik (Dunia 2).

View pelaporan kini memakai cast langsung (`NULLIF(…)::date`, `::numeric`) alih-alih `CASE` + regex — aman karena data hanya ditulis lewat `DocumentService` yang menormalkan nilai; agregasi 1 juta baris 1.970 ms → ±860 ms.

**Muatan outbox** (versi bentuk `payloadVersion = 2`), fondasi proyeksi Dunia 2: selain identitas dokumen, setiap event membawa `version`, `schemaVersion`, `docStatus`, `workflowState`, `workflowVersion`, `amendedFrom`, `occurredAt`, dan **`data` = snapshot JSON dokumen pada versi itu** — proyeksi tidak perlu membaca ulang dokumen yang mungkin sudah berubah.

**Retensi outbox**: pesan yang sudah diproses dihapus setelah `Outbox:RetentionDays` (default 7), per batch tiap jam. Dead-letter tidak dihapus.

**Database analitik terpisah**: `ConnectionStrings:DocFrameAnalytics` mengarahkan view pelaporan + tabel sesi (isolasi tenant) ke instance lain — mis. **replika logis** dari `docframe_documents`. Replika **fisik** tidak didukung karena read-only: tabel sesi tidak bisa ditulis di sana. Lihat `deploy/sql/analytics-logical-replica.sql`.

## PostgreSQL (mode pilot)

### Menjalankan

```bash
docker compose up -d                                    # PostgreSQL 17 di localhost:5432 (db/user/password: docframe)
dotnet run --project src/Apps.Sample --Persistence:Provider=PostgreSql
```

Atau atur di `appsettings.json` / environment variable:

```json
"Persistence": { "Provider": "PostgreSql" },
"ConnectionStrings": { "DocFrame": "Host=localhost;Port=5432;Database=docframe;Username=docframe;Password=docframe" }
```

Saat startup aplikasi otomatis menerapkan migrasi skema (`Migrations/*.sql`), lalu mengisi data contoh **hanya bila database masih kosong**. Status database bisa dicek di `GET /health`.

### Apa yang tersimpan

| Tabel | Isi |
|---|---|
| `docframe_documents` | Semua dokumen: kolom standar (tenant, nama, owner, status, versi, …) + field DocType di kolom `data` (JSONB, index GIN) |
| `docframe_series` | Nomor seri per tenant/DocType/prefix |
| `docframe_audit` | Riwayat perubahan, transisi workflow, dan pencetakan |
| `docframe_outbox` | Event untuk diproses setelah commit (notifikasi, integrasi) |
| `docframe_notifications` | Kotak masuk pengguna |
| `docframe_metadata_versions` | Versi DocType hasil publish (immutable, satu versi aktif per DocType) |
| `docframe_schema_migrations` | Migrasi yang sudah diterapkan |

### Jaminan yang diuji

- **Atomik**: dokumen, audit, dan event outbox disimpan dalam satu transaksi. Bila salah satu gagal (mis. konflik versi), semuanya dibatalkan.
- **Konflik versi antar-instance**: `UPDATE … WHERE version = @expected`. Dua instance yang mengubah dokumen yang sama → tepat satu berhasil.
- **Nomor seri unik** saat insert paralel dari beberapa instance (`INSERT … ON CONFLICT DO UPDATE … RETURNING`).
- **Outbox aman multi-instance**: pesan diklaim dengan `FOR UPDATE SKIP LOCKED` + kunci sewa 2 menit. Instance yang mati di tengah proses tidak membuat pesan hilang; pesan diproses ulang setelah kunci kedaluwarsa.
- **Migrasi aman multi-instance**: dikunci `pg_advisory_lock`, tiap file dalam satu transaksi.
- **Tanpa SQL dinamis dari input**: nilai filter, kata pencarian, dan bahkan nama field data dikirim sebagai parameter (`data ->> $n`); kolom standar dari peta tetap; operator dan arah sort dari allowlist. Wildcard `%`/`_` di pencarian di-escape.
- **Metadata permanen**: versi hasil publish tetap aktif setelah restart. File JSON dengan versi lebih tinggi (deploy baru) otomatis dipublikasikan ke database.

### Menjalankan test terhadap PostgreSQL

```bash
DOCFRAME_TEST_PG="Host=localhost;Database=docframe_test;Username=docframe;Password=docframe" \
  dotnet run --project tests/Framework.Tests
```

Seluruh test fungsional dijalankan ulang di PostgreSQL (database di-reset per test), ditambah 6 test khusus: data & metadata bertahan setelah restart, update bersamaan dari dua instance, nomor seri paralel, outbox dua worker, injeksi SQL, dan rollback transaksi. **Gunakan database khusus test** — schema `public` dihapus setiap test.

### Catatan operasional untuk pilot

- **Backup**: `pg_dump -Fc docframe > docframe.dump` terjadwal; uji restore secara berkala.
- **Lebih dari satu instance web**: Blazor Server membutuhkan *sticky session* di load balancer, dan kunci Data Protection perlu disimpan bersama (mis. `AddDataProtection().PersistKeysToFileSystem(...)` pada volume bersama) agar cookie tetap valid antar-instance.
- **Isolasi tenant** saat ini ditegakkan di aplikasi (setiap query memakai `tenant_id`). Untuk lapisan pertahanan tambahan, Row-Level Security PostgreSQL bisa ditambahkan sebagai migrasi berikutnya.
- **Strategi penyimpanan**: semua DocType memakai tabel generik + JSONB (RFC §7A). Untuk DocType bervolume tinggi atau yang sering dilaporkan, tabel relasional khusus bisa ditambahkan kemudian tanpa mengubah `DocumentService`.

## Skenario demo

Di lingkungan Development, akun demo dibuat otomatis. Login di `/account/login` dengan password `DocFrame#2026` (bisa diubah di `appsettings.Development.json`, `Demo:Password`).

| Pengguna | Role | Departemen | Yang bisa dicoba |
|---|---|---|---|
| Andi | Employee | IT | Buat pengajuan, tambah item (total dihitung langsung), ajukan |
| Sari | Employee | Marketing | Tidak bisa melihat pengajuan Andi (dokumen milik orang lain "tidak ditemukan") |
| Budi | Manager | IT | Menyetujui pengajuan IT ≤ Rp10 juta; di atasnya diteruskan ke Finance |
| Maya | Manager | Marketing | Hanya melihat pengajuan Marketing (row scope `match:department`) |
| Dewi | Finance | Finance | Menyetujui/menolak tahap Finance (wajib komentar), report + ekspor CSV |
| Rina | Procurement | Operasional | Tab "Pengadaan" (field permLevel 1), membatalkan pengajuan yang disetujui |
| Admin | System Manager | — | Editor metadata: periksa diff, publish versi baru, outbox diagnostik |
| Tono | Manager | IT, **tenant globex** | Tidak melihat apa pun dari tenant acme |

Alur lengkap yang bisa dicoba:

1. Andi membuat dan mengajukan pengajuan.
2. Notifikasi masuk ke kotak masuk Budi melalui outbox.
3. Budi menyetujui.
4. Riwayat audit mencatat actor, state asal/tujuan, dan versi workflow.

Pengajuan yang ditolak bisa di-*amend* menjadi draft baru, misalnya `PR-2026-00005-1`.

## Struktur

```text
src/
  Framework.Core/          .NET murni — tanpa ASP.NET/Blazor
    Expressions/           lexer → AST → interpreter; fungsi allowlist; batas panjang/kedalaman/langkah
    Metadata/              DocTypeDef, FieldDef, WorkflowDef; field type registry; compiler; diff; registry berversi
    Security/              UserContext, PermissionService (aksi, row scope, permLevel field)
    Validation/            DocumentEvaluator (dipakai server & UI), ValidationEngine
    Documents/             Document, hook lifecycle, WorkflowEngine, DocumentService (pipeline save)
    Persistence/           kontrak store terstruktur + implementasi in-memory transaksional
    Jobs/                  OutboxProcessor (retry, backoff, dead-letter), ReportService + CSV
  Framework.Web/           adapter: DI, API generik, UI Blazor Blueprint (layout, list, form dinamis, child table, Link combobox, …)
  Apps.Sample/
    Metadata/*.json        Employee, Vendor, PurchaseRequest (+Item), workflow persetujuan
    Domain/Procurement.cs  handler C#, validator NPWP, notifikasi, report, data contoh
tests/Framework.Tests/     harness uji in-process (tanpa paket tambahan; mudah dipindah ke xUnit)
```

## Pemetaan ke RFC

| RFC | Di prototipe |
|---|---|
| §2.8 Tanpa `eval`/SQL dari metadata | `Expressions.cs`: parser sendiri, fungsi allowlist (`empty`, `len`, `sum`, `round`, `abs`, `coalesce`, `today`, `has_role`), batas sumber daya |
| §5 Model metadata terpisah | `Definitions.cs`: DocumentDef, NamingDef, ListViewDef, PermissionDef, ApiDef, WorkflowDef |
| §6 Urutan save eksplisit | `DocumentService`: authorize → load + cek versi → normalize → tolak mass-assignment → hook → fetchFrom/compute → validate → commit atomik (dokumen + audit + outbox) |
| §6 Hook opsional | Interface kecil (`IBeforeSubmit`, …) + `DocumentHandlerBase` virtual |
| §8 Compiler | Validasi nama, tipe, Link/child, ekspresi, siklus compute (topological sort), permission, list view, workflow |
| §9 Compute tidak dipercaya dari client | Field turunan diabaikan dari payload dan dihitung ulang server (diuji) |
| §10 Permission | Aksi per role; scope `all`/`owner`/`match:field=atribut`; permLevel field (tidak dikirim, tidak bisa ditulis, disembunyikan dari audit); IDOR → 404 |
| §11 Workflow | State machine dengan role, kondisi ekspresi, komentar wajib, docStatus per state; transisi dicatat dengan versi workflow |
| §12 Outbox | Ditulis dalam commit yang sama; worker memproses dengan idempotency key, retry eksponensial, dead-letter |
| §13 List/API/Report | Filter & sort hanya dari allowlist; API opt-in per DocType dengan `If-Match`; report provider C# memakai scope yang sama; CSV aman dari formula injection |
| §14 Tenant | Tenant dari identitas; semua query, lookup, report, dan job ter-scope (diuji) |
| §15 Versioning | Snapshot immutable; diff aman / perlu transformasi / berisiko; risiko wajib dikonfirmasi; dokumen mencatat `SchemaVersion` |

## API

Klien API login lebih dulu untuk mendapat bearer token (berlaku 1 jam):

```bash
TOKEN=$(curl -s -X POST -H "Content-Type: application/json" -d '{"userName":"andi","password":"DocFrame#2026"}' \
  http://localhost:5080/api/auth/login | jq -r .accessToken)
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/auth/me
curl -H "Authorization: Bearer $TOKEN" "http://localhost:5080/api/data/PurchaseRequest?filters=[[\"priority\",\"=\",\"Tinggi\"]]&sort=total&dir=desc"
curl -X PATCH -H "Authorization: Bearer $TOKEN" -H "If-Match: 1" -H "Content-Type: application/json" \
     -d '{"priority":"Rendah"}' http://localhost:5080/api/data/PurchaseRequest/PR-2026-00001
curl -X POST -H "Authorization: Bearer $TOKEN" -H "If-Match: 2" -H "Content-Type: application/json" \
     -d '{}' "http://localhost:5080/api/data/PurchaseRequest/PR-2026-00001/actions/Ajukan"
```

Permintaan GET juga menerima cookie login browser (mis. tautan unduh PDF/CSV). Permintaan yang **mengubah data** (POST/PATCH/DELETE) wajib memakai bearer token — cookie ditolak 403 sebagai perlindungan CSRF.

Rute lainnya:

- `GET /api/meta/{doctype}`
- `GET /api/meta/{doctype}/versions[/{v}]`
- `GET /api/links/{target}?q=`
- `GET /api/data/{doctype}/{name}/audit`
- `GET /api/reports/{name}[/export.csv]`
- `POST /api/admin/metadata/{preview|publish}`

Semua error memakai `application/problem+json`: 400 payload, 403 izin, 404 tidak ditemukan atau di luar scope, 409 konflik versi atau status, 422 validasi.

## Hooks, Form Actions & Background Scheduler

DocFrame mendukung pola Frappe yang aman untuk .NET 10:
- **Lifecycle Hooks**: `BeforeValidate`, `BeforeInsert`, `BeforeUpdate`, `AfterSave`, `OnLoad`, `BeforeTransition`, `BeforeDelete`, `AfterDelete`, `OnCancel`, dan `OnAmend`. Termasuk fungsi bantu `docs.GetFieldAsync` (baca field ringan ber-izin) dan `ctx.Messages` (pesan peringatan non-batal / setara `msgprint`).
- **Custom Form Actions**: Daftarkan tombol kustom toolbar formulir lewat `IFormAction<TDoc>` dengan penegakan izin caller.
- **Background Scheduler**: Jalankan tugas cron periodik terdistribusi ber-lock lewat `AddScheduledJob<T>` dengan akun sistem terisolasi `UserContext.System`.

Panduan lengkap: [docs/hooks-formactions-scheduler.md](file:///d:/Side%20Projects/DocFrame/docs/hooks-formactions-scheduler.md).

## Batasan prototipe (sengaja) — catatan versi awal

*Daftar ini ditulis untuk versi awal. Untuk kondisi terkini lihat "Status terkini" di atas.*

- **Login**: akun lokal ASP.NET Core Identity. Belum ada SSO (OIDC/Entra ID), autentikasi dua faktor, maupun pemulihan password lewat email; reset dilakukan oleh System Manager.
- **Mode in-memory** (default) kehilangan data saat restart; gunakan mode PostgreSQL untuk pilot.
- **Metadata**: versi hasil publish permanen di mode PostgreSQL; belum ada maker-checker (draft oleh A, approve oleh B) maupun migration plan data saat skema DocType berubah.
- Belum ada: saved views, bulk action, parallel approval, dan lampiran multi-file.
- Field Select/Link/Date di filter list masih sederhana (sama dengan / sejak tanggal).
- Huruf Public Sans dimuat dari Google Fonts, dengan fallback font sistem bila offline.
- Kode prototipe ini diverifikasi terhadap source Blazor Blueprint v4.1.0 (tag `components/v4.1.0`). Restore dari nuget.org belum diuji di lingkungan pengembang; jika ada perbedaan versi paket, sesuaikan `BlazorBlueprintVersion` di `Framework.Web.csproj`.

## Menambah DocType baru

1. Tulis `Metadata/NamaDocType.doctype.json` (opsional `*.workflow.json`).
2. Start aplikasi. Compiler menolak metadata yang tidak valid, lengkap dengan path error-nya.
3. Logika khusus: buat class turunan `DocumentHandlerBase`, lalu daftarkan dengan `AddDocHandler<T>()`.
4. Validator, tipe field, event handler, dan report masing-masing didaftarkan dengan `AddFieldValidator`, `AddFieldType`, `AddEventHandler`, dan `AddReport`.
5. Form action kustom didaftarkan dengan `AddFormAction<TDoc, TAction>()`.
6. Hook siklus hidup dokumen didaftarkan dengan `AddDocHook<TDoc, THook>()`.
7. Tugas rutin terjadwal didaftarkan dengan `AddScheduledJob<TJob>()`.
