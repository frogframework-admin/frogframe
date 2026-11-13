using Framework.Documents;
using Framework.Hooks;

namespace Framework.Tests.TestHooks;

/// <summary>Marker class untuk Employee hooks.</summary>
public sealed class Employee { }

/// <summary>
/// Hook: Saat load (GET) karyawan, isi field turunan atasan.
/// Demonstrasi: OnLoad bisa melakukan multiple reads dengan permission check.
/// </summary>
public sealed class EmployeeOnLoadHook : IOnLoad<Employee>
{
    public async Task OnLoadAsync(HookContext context, CancellationToken ct)
    {
        // Isi atasan_name (normalized) jika ada atasan_id
        if (context.Doc.Data.GetValueOrDefault("atasan_id") is string atasanId)
        {
            try
            {
                var atasan = await context.Docs.GetAsync("Employee", atasanId, ct);
                if (atasan.Data.GetValueOrDefault("employee_name") is string atasanName)
                    context.Doc.Data["atasan_name"] = atasanName;
            }
            catch (DocumentNotFoundException)
            {
                // Atasan sudah dihapus, abaikan
            }
        }

        // Bonus: Isi department title jika ada department link
        if (context.Doc.Data.GetValueOrDefault("department") is string deptCode)
        {
            try
            {
                var dept = await context.Docs.GetAsync("Department", deptCode, ct);
                if (dept.Data.GetValueOrDefault("department_name") is string deptName)
                    context.Doc.Data["_department_display"] = deptName;
            }
            catch (DocumentNotFoundException)
            {
                // Department tidak ditemukan, abaikan
            }
        }
    }
}

/// <summary>
/// Hook: Setelah simpan Employee, jika atasan berubah, catat event untuk audit trail.
/// </summary>
public sealed class EmployeeAfterSaveHook : IAfterSave<Employee>
{
    public Task AfterSaveAsync(HookContext context, CancellationToken ct)
    {
        if (context.Previous is null) return Task.CompletedTask; // Baru insert, skip

        var oldAtasan = context.Previous.Data.GetValueOrDefault("atasan_id");
        var newAtasan = context.Doc.Data.GetValueOrDefault("atasan_id");

        if (!Equals(oldAtasan, newAtasan) && newAtasan is not null)
        {
            // Catat event perubahan atasan
            var evt = new OutboxMessage
            {
                TenantId = context.Doc.TenantId,
                Type = "employee.atasan_changed",
                IdempotencyKey = $"{context.Doc.TenantId}:{context.Doc.Name}:atasan_v{context.Doc.Version}",
                Payload = new()
                {
                    ["name"] = context.Doc.Name,
                    ["old_atasan"] = oldAtasan?.ToString(),
                    ["new_atasan"] = newAtasan.ToString(),
                    ["employee_name"] = context.Doc.Data.GetValueOrDefault("employee_name")?.ToString(),
                },
                CreatedAt = DateTimeOffset.UtcNow
            };
            context.Events.Add(evt);
        }

        return Task.CompletedTask;
    }
}
