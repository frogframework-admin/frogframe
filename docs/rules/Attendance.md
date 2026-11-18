<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Attendance (Attendance)

Aturan yang dijalankan kode saat dokumen Attendance (Attendance) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **AttendanceHook**: Absensi: satu per karyawan per tanggal; jam kerja & keterlambatan dari shift; tautan ke cuti yang disetujui; terkunci setelah payslip bulan itu submit.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `attendance_date`: "The date is required" — terjadi bila `d.Date("attendance_date") tidak ditemukan/kosong` (AttendanceHook, TimeHooks.cs:76)
- `attendance_date`: "{emp.Name} already has attendance {dup.Name} on {D.Iso(date)}" — terjadi bila `dup is not null` (AttendanceHook, TimeHooks.cs:80)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "Approved leave {leave.Name} covers this date; consider status 'On leave'" — terjadi bila `leave is not null && d.Str("status") is "Present" or "Absent"` (AttendanceHook, TimeHooks.cs:100)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
