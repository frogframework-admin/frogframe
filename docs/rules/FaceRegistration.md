<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Face Registration (FaceRegistration)

Aturan yang dijalankan kode saat dokumen Face Registration (FaceRegistration) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **FaceRegistrationHook**: Face registration: consent, encrypted template check (only templates stored through the ESS API decrypt), one active registration per employee (verifying a new one revokes the previous one, see FaceActivated).
- **EssApprovalNotifier**: Inbox notifications for self-service requests: the next approver (supervisor, project approver or the HR role) when a request reaches their stage, and the requester when it is decided. The ESS app shows them and links to the approval.

## Validasi (dokumen ditolak): Saat aksi workflow (approve/reject/...)

- `consent`: "The employee's consent is required to register a face" — terjadi bila `!d.Bool("consent")` (FaceRegistrationHook, EssRequestHooks.cs:54)
- `photo_1`: "At least one face photo is required" — terjadi bila `!(!d.Bool("consent")) && d.Str("photo_1") is null` (FaceRegistrationHook, EssRequestHooks.cs:55)
- `template_file`: "The face template is missing; register again from the ESS app" — terjadi bila `!(!d.Bool("consent")) && !(d.Str("photo_1") is null) && d.Str("template_file") is null` (FaceRegistrationHook, EssRequestHooks.cs:56)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `template_file`: "Face templates need ASP.NET Data Protection, which is not configured" — terjadi bila `templateId is not null && templateId != c.Previous?.Data.Str("template_file")` (FaceRegistrationHook, EssRequestHooks.cs:37)
- `template_file`: "The face template must be registered from the ESS mobile app" — terjadi bila `vector is null` (FaceRegistrationHook, EssRequestHooks.cs:40)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- FaceTemplates: lihat `_shared-FaceTemplates.md`
- Masters: lihat `_shared-Masters.md`
