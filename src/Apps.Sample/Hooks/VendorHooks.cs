using Framework.Documents;
using Framework.Hooks;

namespace Apps.Sample.Hooks;

/// <summary>Marker class untuk Vendor hooks.</summary>
public sealed class Vendor { }

/// <summary>
/// Hook: Sebelum validasi vendor, normalisasi nama dan NPWP (format standar).
/// </summary>
public sealed class VendorBeforeValidateHook : IBeforeValidate<Vendor>
{
    public Task BeforeValidateAsync(HookContext context, CancellationToken ct)
    {
        // Trim dan capitalize nama vendor
        if (context.Doc.Data.GetValueOrDefault("vendor_name") is string name)
            context.Doc.Data["vendor_name"] = System.Globalization.CultureInfo
                .CurrentCulture.TextInfo.ToTitleCase(name.Trim().ToLowerInvariant());

        // Normalisasi NPWP: hapus titik/spasi
        if (context.Doc.Data.GetValueOrDefault("npwp") is string npwp)
            context.Doc.Data["npwp"] = npwp.Replace(".", "").Replace(" ", "").Replace("-", "");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Hook: Sebelum insert vendor baru, validasi format NPWP.
/// </summary>
public sealed class VendorBeforeInsertHook : IBeforeInsert<Vendor>
{
    public Task BeforeInsertAsync(HookContext context, CancellationToken ct)
    {
        var npwp = context.Doc.Data.GetValueOrDefault("npwp") as string;
        if (string.IsNullOrWhiteSpace(npwp)) return Task.CompletedTask; // NPWP opsional

        if (npwp.Length is not (15 or 16) || !npwp.All(char.IsAsciiDigit))
            throw new ValidationFailedException([new FieldError("npwp", "NPWP harus 15 atau 16 digit angka")]);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hook: Sebelum update vendor, jika NPWP berubah, validasi formatnya.
/// </summary>
public sealed class VendorBeforeUpdateHook : IBeforeUpdate<Vendor>
{
    public async Task BeforeUpdateAsync(HookContext context, IReadOnlyList<FieldChange> changes, CancellationToken ct)
    {
        var npwpChange = changes.FirstOrDefault(c => c.Field == "npwp");
        if (npwpChange is null) return;

        var newNpwp = context.Doc.Data.GetValueOrDefault("npwp") as string;
        if (string.IsNullOrWhiteSpace(newNpwp)) return; // Kosong boleh (unset)

        // Format check
        if (newNpwp.Length is not (15 or 16) || !newNpwp.All(char.IsAsciiDigit))
            throw new ValidationFailedException([new FieldError("npwp", "NPWP harus 15 atau 16 digit angka")]);
    }
}

/// <summary>
/// Hook: Saat load vendor, isi field display dengan kategori dan status active.
/// Juga bisa prefill detail dari eksternal API (placeholder).
/// </summary>
public sealed class VendorOnLoadHook : IOnLoad<Vendor>
{
    public Task OnLoadAsync(HookContext context, CancellationToken ct)
    {
        // Isi field helper untuk UI display
        var status = context.Doc.Data.GetValueOrDefault("blacklisted") is true ? "Diblokir" : "Aktif";
        context.Doc.Data["_status_display"] = status;

        // Bisa fetch data dari eksternal API (placeholder)
        // var apiData = await FetchFromExternalVendorRegistryAsync(npwp);
        // context.Doc.Data["_api_verified"] = apiData?.IsValid;

        return Task.CompletedTask;
    }
}

/// <summary>
/// Hook: Setelah simpan vendor, jika status berubah dari aktif ke blokir, 
/// catat event untuk audit dan notifikasi procurement.
/// </summary>
public sealed class VendorAfterSaveHook : IAfterSave<Vendor>
{
    public Task AfterSaveAsync(HookContext context, CancellationToken ct)
    {
        if (context.Previous is null) return Task.CompletedTask; // Baru insert

        var wasActive = context.Previous.Data.GetValueOrDefault("blacklisted") is not true;
        var isNowBlacklisted = context.Doc.Data.GetValueOrDefault("blacklisted") is true;

        // Jika status berubah dari aktif ke blokir
        if (wasActive && isNowBlacklisted)
        {
            var evt = new OutboxMessage
            {
                TenantId = context.Doc.TenantId,
                Type = "vendor.blacklisted",
                IdempotencyKey = $"{context.Doc.TenantId}:{context.Doc.Name}:blacklist_v{context.Doc.Version}",
                Payload = new()
                {
                    ["name"] = context.Doc.Name,
                    ["vendor_name"] = context.Doc.Data.GetValueOrDefault("vendor_name")?.ToString(),
                    ["npwp"] = context.Doc.Data.GetValueOrDefault("npwp")?.ToString(),
                    ["category"] = context.Doc.Data.GetValueOrDefault("category")?.ToString(),
                    ["blocked_by"] = context.User.Id,
                    ["blocked_at"] = DateTimeOffset.UtcNow.ToString("O"),
                },
                CreatedAt = DateTimeOffset.UtcNow
            };
            context.Events.Add(evt);
        }

        return Task.CompletedTask;
    }
}
