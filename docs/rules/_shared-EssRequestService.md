<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama EssRequestService

Dipakai oleh: beberapa proses (job/event).

Self-service requests of the ESS app: face registration, overtime, attendance correction and biodata change. Every document is created and submitted as the employee, so permissions, workflow conditions and hooks apply as on the desk.

## Validasi (dokumen ditolak)

- `consent`: "The employee's consent is required to register a face" — terjadi bila `!input.Consent` (EssRequestService, EssRequests.cs:65)
- `photos`: "Send 1–{MaxFacePhotos} face photos" — terjadi bila `!(!input.Consent) && input.Photos.Count is < 1 or > MaxFacePhotos` (EssRequestService, EssRequests.cs:67)
- `{field}`: "Use yyyy-MM-ddTHH:mm" — terjadi bila `!(string.IsNullOrWhiteSpace(value)) && value.EndsWith('Z') || value.Length > 19 && (value[^6] is '+' or '-')` (EssRequestService, EssRequests.cs:149)
- `{field}`: "Use yyyy-MM-ddTHH:mm" — terjadi bila `!(string.IsNullOrWhiteSpace(value)) && !(value.EndsWith('Z') || value.Length > 19 && (value[^6] is '+' or '-')) && !DateTime.TryParse(value, CultureInfo.InvariantCulture, styles, out var local)` (EssRequestService, EssRequests.cs:151)
- `decision`: "decision must be 'approve' or 'reject'" (EssRequestService, EssRequests.cs:241)
- `comment`: "A comment is required for '{chosen.Action}'" — terjadi bila `!(!ApprovalDocTypes.Contains(docType)) && chosen.RequireComment && string.IsNullOrWhiteSpace(comment)` (EssRequestService, EssRequests.cs:255)
