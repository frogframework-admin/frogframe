<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama FaceTemplates

Dipakai oleh: FaceRegistration.

Face templates (embeddings) produced by the ESS app's face model. Stored encrypted with ASP.NET Data Protection (purpose Purpose), so a copy of the file store or database alone does not reveal biometric data. The plain template is only returned to the employee's own app through the ESS API.

## Validasi (dokumen ditolak)

- `template`: "The face template must have {MinDimensions}–{MaxDimensions} numbers" — terjadi bila `vector is null || vector.Count is < MinDimensions or > MaxDimensions` (FaceTemplates, FaceTemplates.cs:26)
- `template`: "The face template contains invalid numbers" — terjadi bila `vector.Any(v => double.IsNaN(v) || double.IsInfinity(v))` (FaceTemplates, FaceTemplates.cs:28)
