using Framework.Preferences;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    [Test]
    public async Task UserPreferences_InMemory_CrudAndIsolation()
    {
        var store = new InMemoryUserPreferenceStore();

        var prefA = new DataGridPreference
        {
            Columns = new()
            {
                ["name"] = new GridColumnPreference { Visible = true, Width = "150px" },
                ["employee_name"] = new GridColumnPreference { Visible = true, Width = "250px" },
                ["foto"] = new GridColumnPreference { Visible = false, Width = "60px" }
            }
        };

        // Simpan preferensi untuk user1 di tenant1 pada DocType Employee
        await store.SetAsync("tenant1", "user1", "grid:Employee", prefA);

        // Ambil kembali dan verifikasi
        var loaded = await store.GetAsync<DataGridPreference>("tenant1", "user1", "grid:Employee");
        Assert.True(loaded is not null, "preferensi harus ditemukan");
        Assert.Equal(3, loaded!.Columns.Count, "jumlah kolom harus cocok");
        Assert.Equal("150px", loaded.Columns["name"].Width, "lebar name cocok");
        Assert.Equal("250px", loaded.Columns["employee_name"].Width, "lebar employee_name cocok");
        Assert.True(!loaded.Columns["foto"].Visible, "foto harus tersembunyi");

        // Isolasi User: user2 tidak boleh melihat preferensi user1
        var user2Pref = await store.GetAsync<DataGridPreference>("tenant1", "user2", "grid:Employee");
        Assert.True(user2Pref is null, "user2 tidak boleh melihat preferensi user1");

        // Isolasi Tenant: user1 di tenant2 tidak boleh melihat preferensi tenant1
        var tenant2Pref = await store.GetAsync<DataGridPreference>("tenant2", "user1", "grid:Employee");
        Assert.True(tenant2Pref is null, "tenant2 tidak boleh melihat preferensi tenant1");

        // Isolasi DocType: DocType lain (mis. PurchaseRequest) tidak boleh terpengaruh
        var prPref = await store.GetAsync<DataGridPreference>("tenant1", "user1", "grid:PurchaseRequest");
        Assert.True(prPref is null, "DocType lain harus null");

        // Update preferensi: ubah lebar kolom foto dan tampilkan kembali
        var prefUpdated = new DataGridPreference
        {
            Columns = new()
            {
                ["name"] = new GridColumnPreference { Visible = true, Width = "150px" },
                ["employee_name"] = new GridColumnPreference { Visible = true, Width = "300px" },
                ["foto"] = new GridColumnPreference { Visible = true, Width = "80px" }
            }
        };
        await store.SetAsync("tenant1", "user1", "grid:Employee", prefUpdated);

        var loadedUpdated = await store.GetAsync<DataGridPreference>("tenant1", "user1", "grid:Employee");
        Assert.True(loadedUpdated is not null, "preferensi setelah update harus ditemukan");
        Assert.Equal("300px", loadedUpdated!.Columns["employee_name"].Width, "lebar employee_name diperbarui");
        Assert.True(loadedUpdated.Columns["foto"].Visible, "foto sekarang visible");
        Assert.Equal("80px", loadedUpdated.Columns["foto"].Width, "lebar foto diperbarui");

        // Delete (reset preferensi)
        await store.DeleteAsync("tenant1", "user1", "grid:Employee");
        var afterDelete = await store.GetAsync<DataGridPreference>("tenant1", "user1", "grid:Employee");
        Assert.True(afterDelete is null, "setelah delete harus null (kembali ke default)");
    }

    [Test, PgTest]
    public async Task UserPreferences_Postgres_UpsertAndIsolation()
    {
        var store = sp.GetRequiredService<IUserPreferenceStore>();

        var pref = new DataGridPreference
        {
            Columns = new()
            {
                ["number"] = new GridColumnPreference { Visible = true, Width = "120px" },
                ["department"] = new GridColumnPreference { Visible = false, Width = "200px" }
            }
        };

        // Insert baru
        await store.SetAsync("acme", "user-pg-1", "grid:Employee", pref);

        var retrieved = await store.GetAsync<DataGridPreference>("acme", "user-pg-1", "grid:Employee");
        Assert.True(retrieved is not null, "Postgres: preferensi harus ditemukan");
        Assert.Equal("120px", retrieved!.Columns["number"].Width, "Postgres: lebar number");
        Assert.True(!retrieved.Columns["department"].Visible, "Postgres: visibilitas department");

        // Upsert update
        var pref2 = new DataGridPreference
        {
            Columns = new()
            {
                ["number"] = new GridColumnPreference { Visible = true, Width = "160px" },
                ["department"] = new GridColumnPreference { Visible = true, Width = "220px" }
            }
        };
        await store.SetAsync("acme", "user-pg-1", "grid:Employee", pref2);

        var retrieved2 = await store.GetAsync<DataGridPreference>("acme", "user-pg-1", "grid:Employee");
        Assert.True(retrieved2 is not null, "Postgres: preferensi updated");
        Assert.Equal("160px", retrieved2!.Columns["number"].Width, "Postgres: lebar baru number");
        Assert.True(retrieved2.Columns["department"].Visible, "Postgres: department kini visible");

        // Isolasi pengguna
        var otherUser = await store.GetAsync<DataGridPreference>("acme", "user-pg-2", "grid:Employee");
        Assert.True(otherUser is null, "Postgres: user lain harus null");

        // Delete
        await store.DeleteAsync("acme", "user-pg-1", "grid:Employee");
        var retrievedAfterDel = await store.GetAsync<DataGridPreference>("acme", "user-pg-1", "grid:Employee");
        Assert.True(retrievedAfterDel is null, "Postgres: setelah delete harus null");
    }

    [Test]
    public Task UserPreferences_DataGridPreference_SerializationAndDefaults()
    {
        var pref = new DataGridPreference();
        pref.Columns["name"] = new GridColumnPreference { Visible = true, Width = "12rem" };
        pref.Columns["status"] = new GridColumnPreference { Visible = false, Width = "8rem" };

        var json = System.Text.Json.JsonSerializer.Serialize(pref);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DataGridPreference>(json);

        Assert.True(restored is not null, "deserialized preference tidak boleh null");
        Assert.Equal(2, restored!.Columns.Count, "jumlah kolom 2");
        Assert.Equal("12rem", restored.Columns["name"].Width, "lebar name 12rem");
        Assert.True(restored.Columns["name"].Visible, "kolom name visible");
        Assert.True(!restored.Columns["status"].Visible, "kolom status hidden");
        Assert.Equal("8rem", restored.Columns["status"].Width, "lebar status 8rem");
        return Task.CompletedTask;
    }
}
