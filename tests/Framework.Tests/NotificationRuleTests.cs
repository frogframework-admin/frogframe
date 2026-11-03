using Framework.Documents;
using Framework.Email;
using Framework.Jobs;
using Framework.Notifications;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    private static async Task DrainOutboxAsync(OutboxProcessor processor)
    {
        while ((await processor.ProcessDueAsync()).Processed > 0) { }
    }

    [Test]
    public async Task NotificationRule_OnCreate_memicu_inbox_ke_role_penerima()
    {
        var ruleStore = sp.GetRequiredService<INotificationRuleStore>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        await DrainOutboxAsync(processor);

        // 1. Buat rule: Setiap PR dibuat, berikan notifikasi in-app ke Finance
        var rule = new NotificationRule
        {
            Id = "rule_pr_create_test",
            TenantId = "acme",
            Name = "Alert PR Dibuat",
            DocType = PR,
            Trigger = NotificationTrigger.OnCreate,
            Channel = NotificationChannel.InApp,
            ReceiverType = NotificationReceiverType.Role,
            ReceiverValue = "Finance",
            SubjectTemplate = "PR Baru: {{name}}",
            MessageTemplate = "{{name}} dibuat oleh {{owner}} untuk {{vendor}}.",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await ruleStore.SaveAsync(rule);

        // 2. Andi membuat draft PR baru
        var doc = await Docs.InsertAsync(U("andi"), PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 500_000m)
        });

        // 3. Proses outbox event
        await DrainOutboxAsync(processor);

        // 4. Verifikasi: Dewi (Finance) menerima notifikasi di inbox
        var dewiNotifs = inbox.ForUser("acme", "dewi");
        var prNotif = dewiNotifs.FirstOrDefault(n => n.Subject == $"PR Baru: {doc.Name}");
        Assert.True(prNotif is not null, "Dewi (Finance) menerima notifikasi");
        Assert.Equal(doc.DocType, prNotif!.DocType, "DocType cocok");
        Assert.Equal(doc.Name, prNotif.DocName, "DocName cocok");
        Assert.True(prNotif.Body.Contains("dibuat oleh andi"), "Template pesan ter-render");
    }

    [Test]
    public async Task NotificationRule_OnSubmit_dengan_kondisi_ekspresi()
    {
        var ruleStore = sp.GetRequiredService<INotificationRuleStore>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        await DrainOutboxAsync(processor);

        // Rule: Notifikasi ke Manager HANYA jika total > 10.000.000
        var rule = new NotificationRule
        {
            Id = "rule_pr_submit_large_test",
            TenantId = "acme",
            Name = "Alert PR Besar",
            DocType = PR,
            Trigger = NotificationTrigger.OnSubmit,
            Condition = "total > 10000000",
            Channel = NotificationChannel.InApp,
            ReceiverType = NotificationReceiverType.Role,
            ReceiverValue = "Manager",
            SubjectTemplate = "PR Besar Disubmit: {{name}}",
            MessageTemplate = "Pengajuan {{name}} bernilai Rp{{total}}.",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await ruleStore.SaveAsync(rule);

        // Case A: Submit PR kecil (2.000.000) -> tidak boleh memicu
        var smallPR = await Docs.InsertAsync(U("andi"), PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Rendah",
            ["items"] = Items(2, 1_000_000m)
        });
        await Docs.RunActionAsync(U("andi"), PR, smallPR.Name, "Ajukan", smallPR.Version, comment: null);
        await DrainOutboxAsync(processor);

        var budiNotifs = inbox.ForUser("acme", "budi");
        Assert.True(!budiNotifs.Any(n => n.Subject == $"PR Besar Disubmit: {smallPR.Name}"), "PR kecil tidak memicu notifikasi PR Besar");

        // Case B: Submit PR besar (25.000.000) -> harus memicu!
        var bigPR = await Docs.InsertAsync(U("andi"), PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Tinggi",
            ["justification"] = "Penggantian laptop dan server",
            ["items"] = Items(1, 25_000_000m)
        });
        await Docs.RunActionAsync(U("andi"), PR, bigPR.Name, "Ajukan", bigPR.Version, comment: null);
        await DrainOutboxAsync(processor);

        var budiNotifsAfter = inbox.ForUser("acme", "budi");
        Assert.True(budiNotifsAfter.Any(n => n.Subject == $"PR Besar Disubmit: {bigPR.Name}"), "PR besar lolos kondisi ekspresi dan memicu notifikasi");
    }

    [Test]
    public async Task NotificationRule_OnValueChange_hanya_memicu_saat_field_berubah()
    {
        var ruleStore = sp.GetRequiredService<INotificationRuleStore>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        await DrainOutboxAsync(processor);

        // Rule: Notifikasi ke Owner jika priority berubah
        var rule = new NotificationRule
        {
            Id = "rule_pr_val_change_test",
            TenantId = "acme",
            Name = "Alert Perubahan Prioritas",
            DocType = PR,
            Trigger = NotificationTrigger.OnValueChange,
            TriggerField = "priority",
            Channel = NotificationChannel.InApp,
            ReceiverType = NotificationReceiverType.Owner,
            SubjectTemplate = "Prioritas Diubah: {{name}}",
            MessageTemplate = "Prioritas baru adalah {{priority}}.",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await ruleStore.SaveAsync(rule);

        var doc = await Docs.InsertAsync(U("andi"), PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 100_000m)
        });
        await DrainOutboxAsync(processor);

        // Update field lain (bukan priority) -> tidak memicu
        var upd1 = await Docs.UpdateAsync(U("andi"), PR, doc.Name, doc.Version, new Dictionary<string, object?>
        {
            ["justification"] = "Update keterangan saja"
        });
        await DrainOutboxAsync(processor);

        var andiNotifs1 = inbox.ForUser("acme", "andi");
        Assert.True(!andiNotifs1.Any(n => n.Subject == $"Prioritas Diubah: {doc.Name}"), "update tanpa ganti prioritas tidak memicu notifikasi");

        // Update field priority -> Normal menjadi Tinggi -> memicu!
        await Docs.UpdateAsync(U("andi"), PR, doc.Name, upd1.Version, new Dictionary<string, object?>
        {
            ["priority"] = "Tinggi",
            ["justification"] = "Prioritas urgent penggantian"
        });
        await DrainOutboxAsync(processor);

        var andiNotifs2 = inbox.ForUser("acme", "andi");
        Assert.True(andiNotifs2.Any(n => n.Subject == $"Prioritas Diubah: {doc.Name}"), "perubahan prioritas memicu notifikasi ke Owner");
    }

    [Test]
    public async Task NotificationRule_Email_memasukkan_pesan_ke_outbox_email()
    {
        var ruleStore = sp.GetRequiredService<INotificationRuleStore>();
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var processor = new OutboxProcessor(outboxStore, sp.GetServices<IEventHandler>(), TimeProvider.System);
        await DrainOutboxAsync(processor);

        // Rule: Kirim Email ke user tertentu saat dokumen dibuat
        var rule = new NotificationRule
        {
            Id = "rule_email_test",
            TenantId = "acme",
            Name = "Email Alert PR",
            DocType = PR,
            Trigger = NotificationTrigger.OnCreate,
            Channel = NotificationChannel.Email,
            ReceiverType = NotificationReceiverType.SpecificUser,
            ReceiverValue = "andi",
            SubjectTemplate = "[EMAIL] New PR {{name}}",
            MessageTemplate = "Halo {{actor}}, draft PR {{name}} telah tercatat di sistem.",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await ruleStore.SaveAsync(rule);

        // Insert dokumen
        var doc = await Docs.InsertAsync(U("andi"), PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["items"] = Items(1, 150_000m)
        });

        // Jalankan processor untuk memproses event document.inserted
        await DrainOutboxAsync(processor);

        // Verifikasi ada pesan outbox bertipe email.send
        var pendingEmails = outboxStore.Snapshot()
            .Where(m => m.Type == OutboxEmailQueue.EventType && m.Payload.GetValueOrDefault("subject") == $"[EMAIL] New PR {doc.Name}")
            .ToList();

        Assert.True(pendingEmails.Count > 0, "pesan email masuk ke outbox");
        var emailPayload = pendingEmails[0].Payload;
        Assert.True(emailPayload.ContainsKey("to"), "payload email memuat to");
        Assert.True(emailPayload["body_html"]!.Contains($"draft PR {doc.Name}"), "body html email memuat detail PR");
    }

    [Test]
    public async Task NotificationRule_DateTrigger_job_mengirim_notifikasi_sesuai_offset()
    {
        var ruleStore = sp.GetRequiredService<INotificationRuleStore>();
        var inbox = sp.GetRequiredService<INotificationStore>();
        var store = sp.GetRequiredService<IDocumentStore>();
        var engine = sp.GetRequiredService<NotificationRuleEngine>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var targetDueDate = today.AddDays(2).ToString("yyyy-MM-dd");

        // Rule: 2 hari sebelum required_by, kirim notifikasi ke Finance
        var rule = new NotificationRule
        {
            Id = "rule_date_trigger_test",
            TenantId = "acme",
            Name = "Pengingat Jatuh Tempo PR",
            DocType = PR,
            Trigger = NotificationTrigger.DaysBeforeDate,
            TriggerField = "required_by",
            DaysOffset = 2,
            Channel = NotificationChannel.InApp,
            ReceiverType = NotificationReceiverType.Role,
            ReceiverValue = "Finance",
            SubjectTemplate = "Jatuh Tempo 2 Hari Lagi: {{name}}",
            MessageTemplate = "Pengajuan {{name}} perlu penyelesaian sebelum {{required_by}}.",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };
        await ruleStore.SaveAsync(rule);

        // Buat PR dengan required_by = today + 2 hari
        var doc = await Docs.InsertAsync(U("andi"), PR, new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001",
            ["priority"] = "Normal",
            ["required_by"] = targetDueDate,
            ["items"] = Items(1, 1_000_000m)
        });

        // Jalankan scheduled job date trigger
        var dateJob = new NotificationRuleDateJob(ruleStore, engine, store, clock);
        var jobContext = new ScheduledJobContext
        {
            TenantId = "acme",
            SystemUser = UserContext.System("acme"),
            Docs = Docs,
            Services = sp,
            ScheduledTime = clock.GetUtcNow()
        };

        await dateJob.ExecuteAsync(jobContext, CancellationToken.None);

        // Verifikasi: Dewi (Finance) menerima pengingat jatuh tempo
        var dewiNotifs = inbox.ForUser("acme", "dewi");
        Assert.True(dewiNotifs.Any(n => n.Subject == $"Jatuh Tempo 2 Hari Lagi: {doc.Name}"), "notifikasi jatuh tempo tanggal berhasil dikirim oleh scheduler");
    }

    [Test]
    public async Task NotificationRule_Store_CRUD_dan_isolasi_tenant()
    {
        var ruleStore = sp.GetRequiredService<INotificationRuleStore>();

        var rAcme = new NotificationRule
        {
            Id = "rule_tenant_acme",
            TenantId = "acme",
            Name = "Acme Rule",
            DocType = PR,
            Trigger = NotificationTrigger.OnCreate,
            Channel = NotificationChannel.InApp,
            ReceiverType = NotificationReceiverType.Owner,
            SubjectTemplate = "Test",
            MessageTemplate = "Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };

        var rGlobex = new NotificationRule
        {
            Id = "rule_tenant_globex",
            TenantId = "globex",
            Name = "Globex Rule",
            DocType = PR,
            Trigger = NotificationTrigger.OnCreate,
            Channel = NotificationChannel.InApp,
            ReceiverType = NotificationReceiverType.Owner,
            SubjectTemplate = "Test",
            MessageTemplate = "Test",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ModifiedAt = DateTimeOffset.UtcNow
        };

        await ruleStore.SaveAsync(rAcme);
        await ruleStore.SaveAsync(rGlobex);

        // List per tenant
        var acmeRules = await ruleStore.ListAsync("acme");
        Assert.True(acmeRules.Any(r => r.Id == "rule_tenant_acme"), "rule acme ditemukan");
        Assert.True(!acmeRules.Any(r => r.Id == "rule_tenant_globex"), "rule globex tidak bocor ke acme");

        var globexRules = await ruleStore.ListAsync("globex");
        Assert.True(globexRules.Any(r => r.Id == "rule_tenant_globex"), "rule globex ditemukan");
        Assert.True(!globexRules.Any(r => r.Id == "rule_tenant_acme"), "rule acme tidak bocor ke globex");

        // Delete
        await ruleStore.DeleteAsync("acme", "rule_tenant_acme");
        Assert.True(await ruleStore.GetAsync("acme", "rule_tenant_acme") is null, "rule acme berhasil dihapus");
        Assert.True(await ruleStore.GetAsync("globex", "rule_tenant_globex") is not null, "rule globex tetap ada");
    }
}
