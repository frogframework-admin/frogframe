namespace Framework.Security;

public static class RoleDefaults
{
    public static IReadOnlyList<RoleDef> CreateDefaultRoles(string tenantId, DateTimeOffset now) =>
    [
        new()
        {
            Id = $"role_{tenantId}_administrator",
            TenantId = tenantId,
            Name = "Administrator",
            Description = "Akses penuh ke seluruh konfigurasi sistem, data, dan manajemen pengguna.",
            IsSystem = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_system_manager",
            TenantId = tenantId,
            Name = "System Manager",
            Description = "Pengelola teknis sistem (DocType, alur kerja, integrasi API).",
            IsSystem = true,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_user",
            TenantId = tenantId,
            Name = "User",
            Description = "Pengguna standar aplikasi.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_employee",
            TenantId = tenantId,
            Name = "Employee",
            Description = "Karyawan internal perusahaan.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_hr",
            TenantId = tenantId,
            Name = "HR",
            Description = "Divisi Human Resources (Karyawan, Departemen, Struktur Organisasi).",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_purchasing",
            TenantId = tenantId,
            Name = "Purchasing",
            Description = "Divisi Pengadaan Barang & Jasa (Purchase Request, Vendor, Produk).",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_manager",
            TenantId = tenantId,
            Name = "Manager",
            Description = "Manajer divisi / atasan persetujuan dokumen.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_finance",
            TenantId = tenantId,
            Name = "Finance",
            Description = "Divisi Keuangan dan verifikasi anggaran.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        },
        new()
        {
            Id = $"role_{tenantId}_procurement",
            TenantId = tenantId,
            Name = "Procurement",
            Description = "Petugas pengadaan dan pemenuhan pesanan.",
            IsSystem = false,
            CreatedAt = now,
            UpdatedAt = now
        }
    ];
}

