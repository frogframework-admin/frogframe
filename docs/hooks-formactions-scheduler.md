# Panduan Arsitektur: Hooks Dokumen, Form Actions & Background Scheduler

> **Status**: Terimplementasi & Teruji (79 pengujian lulus)  
> **Target Framework**: .NET 10, ASP.NET Core, Blazor Server  
> **Kompatibilitas**: Frappe-Parity dengan Standar Keamanan & Performa Enterprise  

Dokumen ini mendokumentasikan tiga kemampuan inti baru di **DocFrame** yang terinspirasi dari pola terbaik di framework Frappe, namun dirancang aman untuk ekosistem .NET 10:
1. **Document Lifecycle Hooks Expansion & Script API Helpers**: Siklus hidup dokumen lengkap (`BeforeDelete`, `AfterDelete`, `OnCancel`, `OnAmend`) dan fungsi bantu C# aman (`GetFieldAsync`, `ctx.Messages`).
2. **Custom Form Actions (`IFormAction<TDoc>`)**: Tombol kustom pada toolbar formulir dokumen yang dieksekusi di server dengan validasi hak akses pemanggil.
3. **Background Scheduler (`AddScheduledJob<T>`)**: Mesin penjadwalan berkala terdistribusi (*distributed locked cron*) yang mengeksekusi tugas otomatis atas nama identitas akun sistem (`UserContext.System`).

---

## 1. Prinsip Keamanan & Desain (Paritas Frappe yang Aman)

Frappe Framework memiliki arsitektur metadata yang sangat fleksibel, namun memiliki beberapa risiko keamanan dan konkurensi bawaan pada implementasi aslinya:
- **Tanpa Evaluasi JavaScript Metadata**: DocFrame tidak mengeksekusi skrip JavaScript dari database di sisi browser klien (mencegah stored XSS). Semua logika dieksekusi secara terkompilasi dalam C# atau lewat *Safe AST Interpreter* berbatas waktu/langkah.
- **Tanpa Raw SQL Bebas (`frappe.db.sql`)**: Modul hook dan aksi tidak diizinkan menjalankan raw SQL string bebas yang dapat melewati isolasi tenant atau memicu *table locks*. Pembacaan data dilakukan lewat `DocumentService` ber-scope tenant atau `IReportingQueryExecutor` (read-only).
- **Tanpa Monkey Patching / Class Override**: Tidak menggunakan `override_doctype_class` yang rapuh terhadap update. Seluruh ekstensi terdaftar rapi melalui *Dependency Injection* (DI) .NET.
- **Penegakan Hak Akses Berlapis**: Operasi form action tetap memakai `UserContext` milik pemanggil; operasi terjadwal memakai akun `UserContext.System` yang tercatat di audit log.

---

## 2. Bagian 1: Document Lifecycle Hooks

Siklus hidup dokumen diperluas sehingga mencakup seluruh tahapan mutasi dokumen dan amandemen alur kerja.

### 2.1 Tahapan Lifecycle (`DocHookStage`)

```csharp
namespace Framework.Hooks;

public enum DocHookStage
{
    BeforeValidate,   // Sebelum validasi skema/aturan
    BeforeInsert,     // Sebelum simpan dokumen baru (setelah validasi)
    BeforeUpdate,     // Sebelum update dokumen lama (setelah validasi)
    AfterSave,        // Setelah commit basis data (bisa kirim Outbox)
    OnLoad,           // Saat dokumen dibaca tunggal (GetAsync)
    BeforeTransition, // Sebelum transisi state alur kerja
    BeforeDelete,     // Sebelum dokumen dihapus dari penyimpanan
    AfterDelete,      // Setelah dokumen berhasil dihapus
    OnCancel,         // Saat dokumen dibatalkan (DocStatus = Cancelled / 2)
    OnAmend           // Saat dokumen diamandemen (membuat draft baru bertaut)
}
```

### 2.2 Antarmuka Hook Khusus

Setiap hook dapat diimplementasikan secara terpisah untuk DocType tertentu (`TDoc`):

```csharp
// Dijalankan sebelum dokumen dihapus. Lempar ValidationFailedException untuk membatalkan penghapusan.
public interface IBeforeDelete<TDoc>
{
    Task BeforeDeleteAsync(HookContext context, CancellationToken ct);
}

// Dijalankan setelah penghapusan berhasil di commit. Cocok untuk emit event atau membersihkan cache.
public interface IAfterDelete<TDoc>
{
    Task AfterDeleteAsync(HookContext context, CancellationToken ct);
}

// Dijalankan saat dokumen mengalami pembatalan / penolakan alur kerja (DocStatus menjadi Cancelled / 2).
public interface IOnCancel<TDoc>
{
    Task OnCancelAsync(HookContext context, CancellationToken ct);
}

// Dijalankan saat amandemen dokumen dibuat (DocActions.Amend).
// Menerima referensi dokumen asal yang dibatalkan dan draft baru yang dihasilkan.
public interface IOnAmend<TDoc>
{
    Task OnAmendAsync(HookContext context, Document amendedDraft, CancellationToken ct);
}
```

### 2.3 Script API Helpers yang Tersedia di `HookContext`

Di dalam hook, objek `HookContext` menyediakan akses aman ke layanan dokumen:

| Fitur | Padanan Frappe | Cara Kerja di DocFrame |
|---|---|---|
| **Baca Dokumen Lengkap** | `frappe.get_doc(dt, name)` | `await context.Docs.GetAsync(context.User, dt, name, ct)` |
| **Daftar Dokumen** | `frappe.get_list(dt, filters)` | `await context.Docs.ListAsync(context.User, dt, req, ct)` |
| **Baca Satu Field Ringan** | `frappe.db.get_value` | `await context.Docs.GetFieldAsync(context.User, dt, name, fieldName, ct)` |
| **Pesan Peringatan Non-Batal** | `frappe.msgprint(msg)` | `context.Messages.Add("Pesan peringatan...")` |
| **Batalkan Transaksi** | `frappe.throw(msg)` | `throw new ValidationFailedException(field, msg)` |
| **Event Latar Belakang** | `frappe.sendmail` / `enqueue` | `context.Events.Add(new OutboxMessage { ... })` |
| **Pengguna Aktif** | `frappe.session.user` | `context.User` (terisolasi per-tenant) |

#### Contoh: Pembacaan Field Ringan (`GetFieldAsync`)
```csharp
// Membaca hanya field 'status' dari Vendor tanpa mendeserialisasi seluruh baris/child table.
// Memverifikasi izin baca field (permlevel) pemanggil.
var status = await context.Docs.GetFieldAsync(context.User, "Vendor", vendorId, "status", ct);
```

#### Contoh: Notifikasi Non-Batal (`context.Messages`)
```csharp
if (totalAmount > 10_000_000)
{
    context.Messages.Add("Perhatian: Nilai pengajuan melebihi Rp 10.000.000, membutuhkan verifikasi Direktur.");
}
// Pesan otomatis terikat ke doc.TransientMessages dan ditampilkan sebagai Toast Info di Blazor UI.
```

### 2.4 Pendaftaran Dependency Injection

Daftarkan hook di `Program.cs` / modul startup menggunakan ekstensi:
```csharp
builder.Services.AddDocHook<PurchaseRequest, PurchaseRequestLifecycleHook>();
```

---

## 3. Bagian 2: Custom Form Actions (`IFormAction<TDoc>`)

Custom Form Action menyediakan cara elegan menambahkan tombol aksi kustom pada toolbar formulir dokumen (`FormPage.razor`) tanpa menyisipkan JavaScript bebas.

### 3.1 Kontrak Antarmuka (`IFormAction<TDoc>`)

```csharp
namespace Framework.Actions;

public interface IFormAction<TDoc> : IFormAction
{
    // ID unik untuk aksi ini (mis. "duplicate", "send_rfq", "recalculate_tax")
    string ActionId { get; }
    
    // Label teks yang tampil pada tombol
    string Label { get; }
    
    // Ikon Lucide (default: "play")
    string Icon => "play";
    
    // Warna tombol: "neutral" (default), "positive", "negative", "pending"
    string Tone => "neutral";
    
    // Kondisi apakah tombol tampil untuk pengguna dan status dokumen saat ini
    bool IsVisible(UserContext user, Document doc);
    
    // Logika bisnis yang dijalankan saat tombol diklik
    Task<FormActionResult> ExecuteAsync(FormActionContext context, CancellationToken ct);
}
```

### 3.2 Hasil Eksekusi (`FormActionResult`)

Aksi dapat mengembalikan hasil:
- `FormActionResult.Ok("Pesan sukses", refresh: true)`: Menampilkan toast sukses dan memuat ulang formulir dokumen.
- `FormActionResult.NavigateTo("/app/purchase-order/PO-2026-001", "Dokumen PO berhasil dibuat")`: Mengarahkan browser ke halaman tujuan.
- `FormActionResult.Fail("Gagal memproses aksi")`: Menampilkan toast peringatan/error.

### 3.3 Contoh Implementasi: Duplikasi Pengajuan Pembelian

```csharp
public sealed class DuplicatePurchaseRequestAction : IFormAction<PurchaseRequest>
{
    public string ActionId => "duplicate";
    public string Label => "Duplikasi Pengajuan";
    public string Icon => "copy";
    public string Tone => "neutral";

    // Tombol hanya muncul jika dokumen sudah di-submit dan pengguna memiliki role "Employee"
    public bool IsVisible(UserContext user, Document doc) =>
        doc.DocStatus != DocStatus.Draft && user.Roles.Contains("Employee");

    public async Task<FormActionResult> ExecuteAsync(FormActionContext context, CancellationToken ct)
    {
        var cloneData = new Dictionary<string, object?>(context.Doc.Data);
        cloneData.Remove("name"); // Hapus nomor dokumen lama agar nomor seri baru dialokasikan
        
        var copy = await context.Docs.InsertAsync(context.User, context.Doc.DocType, cloneData, ct: ct);
        
        return FormActionResult.NavigateTo($"/app/purchaserequest/{copy.Name}", $"Berhasil diduplikasi ke {copy.Name}");
    }
}
```

### 3.4 Pendaftaran DI
```csharp
builder.Services.AddFormAction<PurchaseRequest, DuplicatePurchaseRequestAction>();
```

### 3.5 Tampilan di UI (`FormPage.razor`)
Pada toolbar halaman form:
- Sistem memfilter seluruh aksi yang terdaftar untuk `DocType` dokumen aktif.
- Menjalankan `action.IsVisible(CurrentUser.Required, Doc)`.
- Merender tombol dengan ikon, label, dan *tone* yang sesuai di samping tombol Alur Kerja (*Workflow*).
- Menangani klik secara asinkron dengan pemblokir ganda (*debounce*), menampilkan spinner, toast notifikasi, dan penyegaran data otomatis.

---

## 4. Bagian 3: Background Scheduler Engine

DocFrame memiliki mesin penjadwalan latar belakang yang tangguh untuk menjalankan tugas rutin seperti:
- Menutup pengajuan yang kadaluarsa (> 14 hari tidak diproses).
- Mengirim rekap ringkasan harian / mingguan.
- Menyinkronkan status pembayaran atau inventaris.

### 4.1 Antarmuka Job (`IScheduledJob`)

```csharp
namespace Framework.Jobs;

public interface IScheduledJob
{
    string Name { get; }
    
    /// <summary>
    /// Ekspresi cron standar 5-field ("0 0 * * *") atau shorthand:
    /// @daily, @hourly, @every_minute, @every_5m, @every_15m, @every_30m
    /// </summary>
    string Schedule { get; }
    
    Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct);
}
```

### 4.2 Konteks Eksekusi (`ScheduledJobContext`)

Setiap job menerima konteks yang aman dan terisolasi:
```csharp
public sealed class ScheduledJobContext
{
    public required string TenantId { get; init; }
    public required UserContext SystemUser { get; init; } // Akun "system" dengan hak akses Administrator
    public required DocumentService Docs { get; init; }
    public required IServiceProvider Services { get; init; }
    public required DateTimeOffset ScheduledTime { get; init; }
    public ISchedulerLogger Logger { get; init; }
}
```

### 4.3 Distributed Locking (`IDistributedLock`)

Untuk mencegah tugas yang sama dieksekusi lebih dari satu kali secara bersamaan pada *cluster* / multi-instance server:
1. Sebelum job dieksekusi untuk suatu tenant, runner mencoba memperoleh kunci terdistribusi:
   `key = "docframe:scheduler:{job.Name}:{tenantId}"`
2. Kunci diambil secara non-blocking (`timeout = TimeSpan.Zero`).
3. Jika instance lain sedang memproses job tersebut, pemanggilan akan dilewati dengan status `SkippedLocked`.
4. Setelah selesai (atau terjadi exception), kunci dilepas secara otomatis (`IDisposable`).

### 4.4 Evaluasi Jadwal Cron (`CronSchedule`)

Mendukung 5 bagian cron standar:
```
┌───────────── menit (0 - 59)
│ ┌───────────── jam (0 - 23)
│ │ ┌───────────── hari dalam bulan (1 - 31)
│ │ │ ┌───────────── bulan (1 - 12)
│ │ │ │ ┌───────────── hari dalam minggu (0 - 6 atau 7, 0/7 = Minggu)
│ │ │ │ │
* * * * *
```

Fitur:
- **Wildcard (`*`)**, **Step (`*/15`)**, **Range (`1-5`)**, dan **Daftar (`0,15,30,45`)**.
- **Shorthand:** `@yearly`, `@monthly`, `@weekly`, `@daily`, `@hourly`, `@every_minute`, `@every_5m`, `@every_10m`, `@every_15m`, `@every_30m`.
- **Deduplikasi dalam satu menit**: Jika job telah selesai dieksekusi pada menit yang sama, runner tidak akan mengeksekusinya lagi meskipun timer polling berdetak tiap beberapa detik.

### 4.5 Contoh Implementasi Job: Pembersihan Dokumen Kadaluarsa

```csharp
public sealed class AutoCloseStaleRequestsJob : IScheduledJob
{
    public string Name => "auto-close-stale-requests";
    public string Schedule => "@daily"; // Berjalan setiap hari pada 00:00 UTC

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var cutoff = context.ScheduledTime.AddDays(-14);
        
        // Query dokumen draft yang tidak diperbarui selama 14 hari
        var response = await context.Docs.ListAsync(context.SystemUser, "PurchaseRequest", new ListRequest(
            Filters: [
                new FilterClause("docstatus", "=", 0),
                new FilterClause("modified", "<", cutoff)
            ],
            PageSize: 100
        ), ct);

        foreach (var item in response.Items)
        {
            var name = item["name"]?.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            // Jalankan aksi pembatalan sebagai akun System
            await context.Docs.RunActionAsync(
                context.SystemUser, 
                "PurchaseRequest", 
                name, 
                "Batalkan", 
                expectedVersion: Convert.ToInt32(item["version"]), 
                comment: "Dibatalkan otomatis oleh sistem karena melewati batas 14 hari.",
                ct: ct
            );
        }
    }
}
```

### 4.6 Pendaftaran & Konfigurasi Host

Daftarkan job di `Program.cs`:
```csharp
builder.Services.AddScheduledJob<AutoCloseStaleRequestsJob>();
```

Interval polling scheduler dapat diatur di `appsettings.json`:
```json
{
  "Scheduler": {
    "PollIntervalSeconds": 15
  }
}
```

---

## 5. Ringkasan Ekstensi DI (`Framework.Web/Hosting.cs`)

| Ekstensi DI | Fungsi |
|---|---|
| `services.AddDocHook<TDoc, THook>()` | Mendaftarkan hook siklus hidup dokumen untuk DocType `TDoc`. |
| `services.AddFormAction<TDoc, TAction>()` | Mendaftarkan aksi kustom formulir untuk DocType `TDoc`. |
| `services.AddScheduledJob<TJob>()` | Mendaftarkan tugas berkala otomatis ber-lock terdistribusi. |
| `services.AddDocHandler<T>()` | Mendaftarkan handler dokumen terkompilasi bawaan. |
| `services.AddApiMethod<T>()` | Mendaftarkan method RPC kustom (`POST /api/method/{nama}`). |

---

## 6. Verifikasi Pengujian

Seluruh kemampuan diuji menggunakan suite pengujian otomatis di `tests/Framework.Tests`:

```bash
# Menjalankan pengujian Hook & Form Action
dotnet run --project tests/Framework.Tests/Framework.Tests.csproj Hook
dotnet run --project tests/Framework.Tests/Framework.Tests.csproj FormAction

# Menjalankan pengujian Scheduler & Distributed Lock
dotnet run --project tests/Framework.Tests/Framework.Tests.csproj Scheduled
dotnet run --project tests/Framework.Tests/Framework.Tests.csproj CronSchedule
dotnet run --project tests/Framework.Tests/Framework.Tests.csproj DistributedLock

# Menjalankan seluruh rangkaian tes (79 pengujian unit/integrasi)
dotnet run --project tests/Framework.Tests/Framework.Tests.csproj
```
