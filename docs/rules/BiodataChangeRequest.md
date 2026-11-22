<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Biodata Change Request (BiodataChangeRequest)

Aturan yang dijalankan kode saat dokumen Biodata Change Request (BiodataChangeRequest) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **BiodataChangeRequestHook**: Biodata change: only the fields of the chosen category, values checked against the employee field (options, e-mail, child-table columns); once approved by HR the employee record is updated (BiodataApply).
- **EssApprovalNotifier**: Inbox notifications for self-service requests: the next approver (supervisor, project approver or the HR role) when a request reaches their stage, and the requester when it is decided. The ESS app shows them and links to the approval.

## Validasi (dokumen ditolak): Pemeriksaan NormalizeValue

- `{path}`: "{f.Label}: send the rows as a JSON array" (BiodataChangeRequestHook, EssRequestHooks.cs:223)
- `{path}`: "{f.Label}: unknown column '{k}'" — terjadi bila `!child.ByName.TryGetValue(k, out var cf) || cf.IsLayout` (BiodataChangeRequestHook, EssRequestHooks.cs:230)
- `{path}`: "{f.Label}: choose one of {string.Join(", ", options)}" — terjadi bila `!(string.IsNullOrEmpty(value)) && f.Def.Type == "Select" && f.Def.Options is { Count: > 0 } options && !options.Contains(value)` (BiodataChangeRequestHook, EssRequestHooks.cs:246)
- `{path}`: "{f.Label}: not a valid e-mail address" — terjadi bila `!(string.IsNullOrEmpty(value)) && f.Def.Type == "Email" && !System.Net.Mail.MailAddress.TryCreate(value, out _)` (BiodataChangeRequestHook, EssRequestHooks.cs:248)
- `{path}`: "NIK must be 16 digits" — terjadi bila `!(string.IsNullOrEmpty(value)) && f.Name is "nik" && (value.Length != 16 || !value.All(char.IsDigit))` (BiodataChangeRequestHook, EssRequestHooks.cs:250)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `category`: "Choose a category" — terjadi bila `!Fields.TryGetValue(category, out var allowed)` (BiodataChangeRequestHook, EssRequestHooks.cs:188)
- `changes`: "Add at least one change" — terjadi bila `!(!Fields.TryGetValue(category, out var allowed)) && rows.Count == 0` (BiodataChangeRequestHook, EssRequestHooks.cs:191)
- `changes[{i}].field`: "'{field}' cannot be changed under {category}. Allowed: {string.Join(", ", allowed)}" — terjadi bila `!allowed.Contains(field) || !empMeta.ByName.TryGetValue(field, out var f)` (BiodataChangeRequestHook, EssRequestHooks.cs:199)
- `changes[{i}].field`: "'{field}' appears twice" — terjadi bila `!seen.Add(field)` (BiodataChangeRequestHook, EssRequestHooks.cs:200)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
