using Framework.Documents;
using Framework.Hooks;

namespace Apps.Sample.Hooks;

/// <summary>Marker class untuk Department hooks.</summary>
public sealed class Department { }

/// <summary>
/// Hook: Sebelum validasi, normalisasi field kode ke uppercase (standardisasi).
/// </summary>
public sealed class DepartmentBeforeValidateHook : IBeforeValidate<Department>
{
    public Task BeforeValidateAsync(HookContext context, CancellationToken ct)
    {
        if (context.Doc.Data.GetValueOrDefault("code") is string code)
            context.Doc.Data["code"] = code.Trim().ToUpperInvariant();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hook: Sebelum insert department baru, cek struktur organisasi:
/// - Jika ada parent_department, parent harus ada
/// - Cegah circular reference (parent tidak boleh indirect child dari dokumen ini)
/// </summary>
public sealed class DepartmentBeforeInsertHook : IBeforeInsert<Department>
{
    public async Task BeforeInsertAsync(HookContext context, CancellationToken ct)
    {
        if (context.Doc.Data.GetValueOrDefault("parent_department") is not string parentCode)
            return; // Departemen root, boleh

        // Cek parent ada
        try
        {
            _ = await context.Docs.GetAsync("Department", parentCode, ct);
        }
        catch (DocumentNotFoundException)
        {
            throw new ValidationFailedException([new FieldError("parent_department", $"Parent departemen '{parentCode}' tidak ditemukan")]);
        }
    }
}

/// <summary>
/// Hook: Sebelum update department, validasi jika parent berubah:
/// - Cegah self-parent (parent tidak boleh = dokumen sendiri)
/// - Cegah circular hierarchy
/// </summary>
public sealed class DepartmentBeforeUpdateHook : IBeforeUpdate<Department>
{
    public async Task BeforeUpdateAsync(HookContext context, IReadOnlyList<FieldChange> changes, CancellationToken ct)
    {
        const string parentField = "parent_department";
        var change = changes.FirstOrDefault(c => c.Field == parentField);
        if (change is null) return;

        var newParent = context.Doc.Data.GetValueOrDefault("parent_department") as string;
        if (string.IsNullOrWhiteSpace(newParent)) return; // Ubah ke root, boleh

        // Cek self-parent
        if (newParent == context.Doc.Name)
            throw new ValidationFailedException([new FieldError("parent_department", "Departemen tidak boleh menjadi parent dari dirinya sendiri")]);

        // Cek circular: walk up parent chain dari newParent, harus tidak ketemu current doc
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = newParent;
        while (!string.IsNullOrEmpty(current) && visited.Add(current))
        {
            if (current == context.Doc.Name)
                throw new ValidationFailedException([new FieldError("parent_department", $"Struktur departemen akan membentuk siklus jika '{newParent}' menjadi parent")]);

            try
            {
                var dept = await context.Docs.GetAsync("Department", current, ct);
                current = dept.Data.GetValueOrDefault("parent_department") as string;
            }
            catch (DocumentNotFoundException)
            {
                break; // Parent chain putus, aman
            }
        }
    }
}

/// <summary>
/// Hook: Saat load department, refresh field terkomputasi jika ada.
/// Placeholder untuk konsistensi dengan hook set lain.
/// </summary>
public sealed class DepartmentRefreshHierarchyHook : IOnLoad<Department>
{
    public Task OnLoadAsync(HookContext context, CancellationToken ct)
    {
        // Bisa isi field terkomputasi, mis. full_path atau depth level
        // Untuk sekarang hanya placeholder
        return Task.CompletedTask;
    }
}
