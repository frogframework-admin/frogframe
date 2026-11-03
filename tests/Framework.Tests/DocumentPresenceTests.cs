using Framework.Presence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    [Test]
    public async Task Presence_Heartbeat_mencatat_dan_mengembalikan_pengguna_aktif()
    {
        var clock = new ManualClock();
        var presence = new DocumentPresenceService(clock, TimeSpan.FromSeconds(30));

        var userAndi = U("andi");
        var userBudi = U("budi");

        // 1. Andi membuka dokumen PR-001 (Viewing)
        var users1 = await presence.HeartbeatAsync("acme", PR, "PR-001", "session_andi_1", userAndi, PresenceStatus.Viewing);
        Assert.Equal(1, users1.Count, "1 pengguna aktif");
        Assert.Equal("andi", users1[0].UserId, "user id andi");
        Assert.Equal(PresenceStatus.Viewing, users1[0].Status, "status viewing");

        // 2. Budi membuka dokumen yang sama (Editing)
        var users2 = await presence.HeartbeatAsync("acme", PR, "PR-001", "session_budi_1", userBudi, PresenceStatus.Editing);
        Assert.Equal(2, users2.Count, "2 pengguna aktif bersamaan");

        var active = presence.GetActiveUsers("acme", PR, "PR-001");
        Assert.Equal(2, active.Count, "GetActiveUsers mengembalikan 2 pengguna");
        Assert.True(active.Any(u => u.UserId == "andi" && u.Status == PresenceStatus.Viewing), "andi viewing");
        Assert.True(active.Any(u => u.UserId == "budi" && u.Status == PresenceStatus.Editing), "budi editing");
    }

    [Test]
    public async Task Presence_Transisi_status_dari_Viewing_ke_Editing_memperbarui_dan_memicu_event()
    {
        var clock = new ManualClock();
        var presence = new DocumentPresenceService(clock, TimeSpan.FromSeconds(30));
        var userAndi = U("andi");

        PresenceChange? lastEvent = null;
        using var sub = presence.Subscribe("acme", PR, "PR-001", change =>
        {
            lastEvent = change;
        });

        // 1. Andi pertama kali membuka sebagai Viewing -> subscriber diberitahu
        await presence.HeartbeatAsync("acme", PR, "PR-001", "session_1", userAndi, PresenceStatus.Viewing);
        Assert.True(lastEvent is not null, "event join diterima");
        Assert.Equal(PresenceStatus.Viewing, lastEvent!.ActiveUsers.Single(u => u.SessionId == "session_1").Status, "status awal viewing");

        // 2. Andi mulai mengetik / memodifikasi form -> status beralih ke Editing
        lastEvent = null;
        await presence.HeartbeatAsync("acme", PR, "PR-001", "session_1", userAndi, PresenceStatus.Editing);
        Assert.True(lastEvent is not null, "event transisi status diterima");
        Assert.Equal(PresenceStatus.Editing, lastEvent!.ActiveUsers.Single(u => u.SessionId == "session_1").Status, "status diperbarui ke editing");

        // 3. Heartbeat rutin dengan status yang sama tidak memicu broadcast ganda
        lastEvent = null;
        await presence.HeartbeatAsync("acme", PR, "PR-001", "session_1", userAndi, PresenceStatus.Editing);
        Assert.True(lastEvent is null, "heartbeat tanpa perubahan status tidak memicu broadcast");
    }

    [Test]
    public async Task Presence_Leave_menghapus_pengguna_dan_memberitahu_subscriber()
    {
        var clock = new ManualClock();
        var presence = new DocumentPresenceService(clock, TimeSpan.FromSeconds(30));
        var userAndi = U("andi");
        var userBudi = U("budi");

        await presence.HeartbeatAsync("acme", PR, "PR-001", "s_andi", userAndi, PresenceStatus.Viewing);
        await presence.HeartbeatAsync("acme", PR, "PR-001", "s_budi", userBudi, PresenceStatus.Editing);

        PresenceChange? leaveEvent = null;
        using var sub = presence.Subscribe("acme", PR, "PR-001", change =>
        {
            leaveEvent = change;
        });

        // Budi menutup tab / meninggalkan dokumen
        await presence.LeaveAsync("acme", PR, "PR-001", "s_budi");

        Assert.True(leaveEvent is not null, "subscriber menerima event leave");
        Assert.Equal(1, leaveEvent!.ActiveUsers.Count, "sisa 1 pengguna aktif");
        Assert.Equal("andi", leaveEvent.ActiveUsers[0].UserId, "hanya andi yang tersisa");

        var active = presence.GetActiveUsers("acme", PR, "PR-001");
        Assert.Equal(1, active.Count, "GetActiveUsers konsisten");
        Assert.Equal("andi", active[0].UserId, "budi telah dihapus");
    }

    [Test]
    public async Task Presence_Isolasi_tenant_ketat()
    {
        var clock = new ManualClock();
        var presence = new DocumentPresenceService(clock, TimeSpan.FromSeconds(30));
        var userAndiAcme = U("andi");
        var userTonoGlobex = U("tono");

        // Dokumen dengan DocType dan nama identik di dua tenant berbeda
        await presence.HeartbeatAsync("acme", PR, "PR-001", "s_acme", userAndiAcme, PresenceStatus.Viewing);
        await presence.HeartbeatAsync("globex", PR, "PR-001", "s_globex", userTonoGlobex, PresenceStatus.Editing);

        var acmeUsers = presence.GetActiveUsers("acme", PR, "PR-001");
        var globexUsers = presence.GetActiveUsers("globex", PR, "PR-001");

        Assert.Equal(1, acmeUsers.Count, "acme memiliki 1 user");
        Assert.Equal("andi", acmeUsers[0].UserId, "user acme adalah andi");
        Assert.Equal("acme", acmeUsers[0].TenantId, "tenant acme terisolasi");

        Assert.Equal(1, globexUsers.Count, "globex memiliki 1 user");
        Assert.Equal("tono", globexUsers[0].UserId, "user globex adalah tono");
        Assert.Equal("globex", globexUsers[0].TenantId, "tenant globex terisolasi");
    }

    [Test]
    public async Task Presence_Sesi_kedaluwarsa_otomatis_dibersihkan()
    {
        var clock = new ManualClock();
        var presence = new DocumentPresenceService(clock, TimeSpan.FromSeconds(20));
        var userAndi = U("andi");
        var userBudi = U("budi");

        await presence.HeartbeatAsync("acme", PR, "PR-001", "s_andi", userAndi, PresenceStatus.Viewing);
        await presence.HeartbeatAsync("acme", PR, "PR-001", "s_budi", userBudi, PresenceStatus.Editing);

        // Majukan waktu 10 detik (belum kedaluwarsa)
        clock.Advance(TimeSpan.FromSeconds(10));
        // Andi mengirim heartbeat baru
        await presence.HeartbeatAsync("acme", PR, "PR-001", "s_andi", userAndi, PresenceStatus.Viewing);

        // Majukan waktu 15 detik lagi (total 25 detik sejak heartbeat terakhir Budi, > timeout 20 detik)
        clock.Advance(TimeSpan.FromSeconds(15));

        // Heartbeat Andi berikutnya membersihkan sesi Budi yang telah kedaluwarsa
        var activeAfterTimeout = await presence.HeartbeatAsync("acme", PR, "PR-001", "s_andi", userAndi, PresenceStatus.Viewing);

        Assert.Equal(1, activeAfterTimeout.Count, "sesi budi kedaluwarsa dan dihapus");
        Assert.Equal("andi", activeAfterTimeout[0].UserId, "hanya andi yang aktif");
    }

    [Test]
    public async Task Presence_Multi_tab_pengguna_yang_sama_ditangani_independen()
    {
        var clock = new ManualClock();
        var presence = new DocumentPresenceService(clock, TimeSpan.FromSeconds(30));
        var userAndi = U("andi");

        // Andi membuka di Tab 1 (Viewing) dan Tab 2 (Editing)
        await presence.HeartbeatAsync("acme", PR, "PR-001", "tab_1", userAndi, PresenceStatus.Viewing);
        await presence.HeartbeatAsync("acme", PR, "PR-001", "tab_2", userAndi, PresenceStatus.Editing);

        var active = presence.GetActiveUsers("acme", PR, "PR-001");
        Assert.Equal(2, active.Count, "2 sesi tercatat untuk user yang sama");

        // Andi menutup Tab 1 -> Tab 2 tetap aktif sebagai Editing
        await presence.LeaveAsync("acme", PR, "PR-001", "tab_1");

        var remaining = presence.GetActiveUsers("acme", PR, "PR-001");
        Assert.Equal(1, remaining.Count, "1 sesi tersisa");
        Assert.Equal("tab_2", remaining[0].SessionId, "tab 2 tetap aktif");
        Assert.Equal(PresenceStatus.Editing, remaining[0].Status, "status tab 2 tetap editing");
    }
}

