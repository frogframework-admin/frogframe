<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Organization Unit (OrganizationUnit)

Aturan yang dijalankan kode saat dokumen Organization Unit (OrganizationUnit) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **OrganizationUnitHook**: Organization unit: dynamic unit types, cycle prevention, effective-dated overlap validation, closure checks, materialized path &amp; breadcrumb computation, and automatic path cascade to descendant units.
- **OrganizationUnitActivateAction**: Form action on a Planned OrganizationUnit to activate it on its effective date, close its predecessor version, and migrate active Positions.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `effective_to`: "The effective end date must be on or after the effective start date" — terjadi bila `from is not null && to is not null && to < from` (OrganizationUnitHook, HrHooks.cs:491)
- `parent_org_unit`: "Circular organization hierarchy detected involving unit '{code}'" — terjadi bila `curCode == code || sameBase` (OrganizationUnitHook, HrHooks.cs:516)
- `effective_from`: "Active validity period overlaps with version {other.Name} for unit {baseCode}" — terjadi bila `Hr.Overlaps(from, to, other.Data.Date("effective_from"), other.Data.Date("effective_to"))` (OrganizationUnitHook, HrHooks.cs:540)
- `{status == "Closed" ? "status" : "effective_to"}`: "Cannot close unit {code} while active position {activePositions[0].Name} is still assigned to it" — terjadi bila `!hasSuccessor` (OrganizationUnitHook, HrHooks.cs:560)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "Advisory: unit type '{unitTypeDoc.Name}' recommends parent type '{expectedParent}', but parent '{parentDoc.Name}' has type '{actualParentType}'" — terjadi bila `actualParentType is not null && actualParentType != expectedParent` (OrganizationUnitHook, HrHooks.cs:501)

## Tombol aksi: Activate planned structure

Tombol **Activate planned structure** di form Organization Unit hanya tampil bila `doc.Data.Str("status") == "Planned" && (user.HasRole("HR") || user.IsAdmin)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Form action on a Planned OrganizationUnit to activate it on its effective date, close its predecessor version, and migrate active Positions.

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
