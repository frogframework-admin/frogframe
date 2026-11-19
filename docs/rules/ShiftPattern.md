<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Shift Pattern (ShiftPattern)

Aturan yang dijalankan kode saat dokumen Shift Pattern (ShiftPattern) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ShiftPatternHook**: Shift Pattern: versioned N-day rotational work cycle template; validates sequence completeness and version date overlaps.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `items`: "The cycle sequence has {items.Count} row(s), which must equal the cycle length of {cycleDays} day(s)" — terjadi bila `items.Count != cycleDays` (ShiftPatternHook, WsmHooks.cs:102)
- `items[{i}].day_number`: "Day in cycle must be between 1 and {cycleDays}" — terjadi bila `dayNum < 1 || dayNum > cycleDays` (ShiftPatternHook, WsmHooks.cs:115)
- `items[{i}].day_number`: "Day {dayNum} is defined more than once in the cycle" — terjadi bila `!seenDays.Add(dayNum)` (ShiftPatternHook, WsmHooks.cs:117)
- `items[{i}].shift_type`: "Shift type is required for a WORK day" — terjadi bila `r.Str("shift_type") is null` (ShiftPatternHook, WsmHooks.cs:126)
- `effective_from`: "Active pattern version overlaps with {other.Name} for canonical pattern {baseCode}" — terjadi bila `Hr.Overlaps(from, to, other.Data.Date("effective_from"), other.Data.Date("effective_to"))` (ShiftPatternHook, WsmHooks.cs:139)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
