using Framework.Integrations;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Unit & regression tests for Phase 3 Workstream B: Auto Repeat.</summary>
sealed partial class Tests
{
    [Test]
    public Task AutoRepeat_schedule_frequency_calculations()
    {
        // 1. Daily
        var d1 = new DateOnly(2026, 3, 1);
        Assert.Equal(new DateOnly(2026, 3, 2), AutoRepeatSchedule.Next(d1, AutoRepeatFrequency.Daily), "Daily advances 1 day");

        // 2. Weekly
        Assert.Equal(new DateOnly(2026, 3, 8), AutoRepeatSchedule.Next(d1, AutoRepeatFrequency.Weekly), "Weekly advances 7 days");

        // 3. Monthly regular year (Jan 31 -> Feb 28)
        var jan31 = new DateOnly(2025, 1, 31);
        Assert.Equal(new DateOnly(2025, 2, 28), AutoRepeatSchedule.Next(jan31, AutoRepeatFrequency.Monthly), "Monthly regular year clamps to Feb 28");

        // 4. Monthly leap year (Jan 31 -> Feb 29)
        var jan31Leap = new DateOnly(2024, 1, 31);
        Assert.Equal(new DateOnly(2024, 2, 29), AutoRepeatSchedule.Next(jan31Leap, AutoRepeatFrequency.Monthly), "Monthly leap year clamps to Feb 29");

        // 5. Quarterly (Jan 15 -> Apr 15)
        var q1 = new DateOnly(2026, 1, 15);
        Assert.Equal(new DateOnly(2026, 4, 15), AutoRepeatSchedule.Next(q1, AutoRepeatFrequency.Quarterly), "Quarterly advances 3 months");

        // 6. Yearly
        var y1 = new DateOnly(2026, 5, 20);
        Assert.Equal(new DateOnly(2027, 5, 20), AutoRepeatSchedule.Next(y1, AutoRepeatFrequency.Yearly), "Yearly advances 1 year");

        // 7. Completion check
        var defWithEnd = new AutoRepeatDef
        {
            Id = "rep_test_comp",
            TenantId = "acme",
            Name = "End Date Test",
            ReferenceDocType = PR,
            ReferenceDocName = "PR-001",
            Frequency = AutoRepeatFrequency.Daily,
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 3),
            NextScheduleDate = new DateOnly(2026, 1, 1),
            Status = AutoRepeatStatus.Active,
            RunAsUserId = "andi"
        };

        Assert.True(!AutoRepeatSchedule.IsCompleted(defWithEnd, new DateOnly(2026, 1, 3)), "Date equal to end date is not completed");
        Assert.True(AutoRepeatSchedule.IsCompleted(defWithEnd, new DateOnly(2026, 1, 4)), "Date past end date is marked completed");

        return Task.CompletedTask;
    }

    [Test]
    public Task AutoRepeat_compiler_rejects_child_and_single_doctypes()
    {
        var compiler = sp.GetRequiredService<MetadataCompiler>();

        // 1. Child DocType cannot have AllowAutoRepeat = true
        var childDef = new DocTypeDef
        {
            Name = "TestChildDoc",
            IsChild = true,
            AllowAutoRepeat = true,
            Module = "PROCUREMENT",
            Fields = [new FieldDef { Name = "item_code", Type = "Data", Label = "Code" }]
        };
        var childEx = false;
        try { compiler.Compile([childDef], []); } catch (MetadataCompilationException) { childEx = true; }
        Assert.True(childEx, "Compilation threw MetadataCompilationException for Child DocType with AllowAutoRepeat");

        // 2. Single DocType cannot have AllowAutoRepeat = true
        var singleDef = new DocTypeDef
        {
            Name = "TestSingleDoc",
            IsSingle = true,
            AllowAutoRepeat = true,
            Module = "ADMINISTRATION",
            Fields = [new FieldDef { Name = "config_key", Type = "Data", Label = "Key" }]
        };
        var singleEx = false;
        try { compiler.Compile([singleDef], []); } catch (MetadataCompilationException) { singleEx = true; }
        Assert.True(singleEx, "Compilation threw MetadataCompilationException for Single DocType with AllowAutoRepeat");

        return Task.CompletedTask;
    }

    [Test]
    public async Task AutoRepeat_document_cloning_strips_system_and_nocopy_fields()
    {
        var autoRepeatService = sp.GetRequiredService<AutoRepeatService>();
        var user = U("andi");

        // Insert a source document with child table
        var sourceDoc = await Docs.InsertAsync(user, PR, P(
            ("vendor", "VND-0001"),
            ("request_date", ReqDate()),
            ("required_by", ReqDate(5)),
            ("justification", "Regular monthly order"),
            ("items", new List<Dictionary<string, object?>>
            {
                new() { ["item_name"] = "Item 1", ["qty"] = 1m, ["rate"] = 250000m, ["uom"] = "pcs" },
                new() { ["item_name"] = "Item 2", ["qty"] = 2m, ["rate"] = 125000m, ["uom"] = "pcs" }
            })
        ));

        var meta = sp.GetRequiredService<IMetadataProvider>().Current.Get(PR);
        var scheduledDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        var payload = autoRepeatService.CloneDocumentPayload(meta, sourceDoc, scheduledDate);

        // System fields must NOT be in payload
        Assert.True(!payload.ContainsKey("name"), "System field 'name' was stripped");
        Assert.True(!payload.ContainsKey("owner"), "System field 'owner' was stripped");
        Assert.True(!payload.ContainsKey("creation"), "System field 'creation' was stripped");
        Assert.True(!payload.ContainsKey("modified"), "System field 'modified' was stripped");
        Assert.True(!payload.ContainsKey("docstatus"), "System field 'docstatus' was stripped");
        Assert.True(!payload.ContainsKey("version"), "System field 'version' was stripped");

        // Date field should be set to scheduledDate
        Assert.Equal(scheduledDate.ToString("yyyy-MM-dd"), payload["request_date"], "Transaction date set to scheduledDate");

        // Child rows must NOT contain child system fields
        var itemsObj = payload["items"] as IEnumerable<Dictionary<string, object?>>;
        Assert.True(itemsObj is not null, "Child table cloned");
        var childRows = itemsObj!.ToList();
        Assert.Equal(2, childRows.Count, "Child table rows preserved");
        foreach (var row in childRows)
        {
            Assert.True(!row.ContainsKey("name"), "Child row 'name' stripped");
            Assert.True(!row.ContainsKey("parent"), "Child row 'parent' stripped");
            Assert.True(!row.ContainsKey("idx"), "Child row 'idx' stripped");
            Assert.True(row.ContainsKey("item_name") || row.ContainsKey("rate"), "Child business fields preserved");
        }
    }

    [Test]
    public async Task AutoRepeat_runs_prevent_duplicate_documents_for_same_schedule_date()
    {
        var store = new InMemoryAutoRepeatStore();
        var autoRepeatId = "rep_dup_test";
        var date = new DateOnly(2026, 3, 1);

        var run1 = new AutoRepeatRun
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            AutoRepeatId = autoRepeatId,
            ScheduledFor = date,
            GeneratedDocName = "PR-GEN-01",
            Status = "Success"
        };
        var inserted1 = await store.RecordRunAsync(run1);
        Assert.True(inserted1, "First run recorded successfully");

        // Second attempt for exact same schedule date is rejected by store
        var run2 = new AutoRepeatRun
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            AutoRepeatId = autoRepeatId,
            ScheduledFor = date,
            GeneratedDocName = "PR-GEN-02",
            Status = "Success"
        };
        var inserted2 = await store.RecordRunAsync(run2);
        Assert.True(!inserted2, "Duplicate run for the same date was prevented");

        var runs = await store.ListRunsAsync("acme", autoRepeatId);
        Assert.Equal(1, runs.Count, "Only exactly 1 run exists for the schedule date");
    }

    [Test]
    public async Task AutoRepeat_catchup_generates_documents_up_to_max_limit()
    {
        var autoRepeatStore = new InMemoryAutoRepeatStore();
        var autoRepeatService = new AutoRepeatService(
            autoRepeatStore,
            Docs,
            sp.GetRequiredService<IMetadataProvider>(),
            new AutomationIdentity(sp.GetRequiredService<IUserAccountRepository>()),
            sp.GetRequiredService<IPermissionService>()
        );

        var user = U("andi");
        var baseDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-20);
        var sourceDoc = await Docs.InsertAsync(user, PR, P(
            ("vendor", "VND-0001"),
            ("request_date", baseDate.ToString("yyyy-MM-dd")),
            ("required_by", baseDate.AddDays(5).ToString("yyyy-MM-dd")),
            ("items", Items(1, 100000))
        ));

        // Create repeat overdue by 20 days (Daily recurrence)
        var repeat = new AutoRepeatDef
        {
            Id = "rep_catchup_test",
            TenantId = "acme",
            Name = "Catch-up Test",
            ReferenceDocType = PR,
            ReferenceDocName = sourceDoc.Name,
            Frequency = AutoRepeatFrequency.Daily,
            StartDate = baseDate,
            NextScheduleDate = baseDate,
            Status = AutoRepeatStatus.Active,
            RunAsUserId = "andi"
        };
        await autoRepeatStore.SaveAsync(repeat);

        // Execute as of today (overdue by 20 days)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var summaries = await autoRepeatService.ExecuteDueAsync("acme", today);

        var summary = summaries.FirstOrDefault(s => s.AutoRepeatId == repeat.Id);
        Assert.True(summary is not null, "Summary returned for catchup");
        Assert.Equal(AutoRepeatService.MaxCatchUpPerCycle, summary!.DocumentsGenerated, $"Limited to exactly {AutoRepeatService.MaxCatchUpPerCycle} documents per cycle");

        // Next schedule date should be baseDate + 12 days
        Assert.Equal(baseDate.AddDays(12), summary.NextScheduleDate, "NextScheduleDate advanced by 12 days");

        var runs = await autoRepeatStore.ListRunsAsync("acme", repeat.Id, limit: 100);
        Assert.Equal(12, runs.Count, "12 execution run records created");
    }

    [Test]
    public async Task AutoRepeat_reaches_end_date_and_marks_completed()
    {
        var autoRepeatStore = new InMemoryAutoRepeatStore();
        var autoRepeatService = new AutoRepeatService(
            autoRepeatStore,
            Docs,
            sp.GetRequiredService<IMetadataProvider>(),
            new AutomationIdentity(sp.GetRequiredService<IUserAccountRepository>()),
            sp.GetRequiredService<IPermissionService>()
        );

        var user = U("andi");
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-2);
        var end = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var sourceDoc = await Docs.InsertAsync(user, PR, P(
            ("vendor", "VND-0001"),
            ("request_date", start.ToString("yyyy-MM-dd")),
            ("required_by", start.AddDays(5).ToString("yyyy-MM-dd")),
            ("items", Items(1, 100000))
        ));

        // Start 2 days ago, ends yesterday (Daily = 2 runs total)
        var repeat = new AutoRepeatDef
        {
            Id = "rep_end_date_test",
            TenantId = "acme",
            Name = "End Date Completion Test",
            ReferenceDocType = PR,
            ReferenceDocName = sourceDoc.Name,
            Frequency = AutoRepeatFrequency.Daily,
            StartDate = start,
            EndDate = end,
            NextScheduleDate = start,
            Status = AutoRepeatStatus.Active,
            RunAsUserId = "andi"
        };
        await autoRepeatStore.SaveAsync(repeat);

        // Run as of today
        var summaries = await autoRepeatService.ExecuteDueAsync("acme", DateOnly.FromDateTime(DateTime.UtcNow));
        var summary = summaries.First(s => s.AutoRepeatId == repeat.Id);

        Assert.Equal(2, summary.DocumentsGenerated, "Generated exactly 2 documents up to EndDate");
        Assert.Equal(AutoRepeatStatus.Completed, summary.Status, "Status transitioned to Completed");

        // Subsequent run produces 0 documents because status is Completed
        var secondRun = await autoRepeatService.ExecuteDueAsync("acme", DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));
        Assert.True(secondRun.All(s => s.AutoRepeatId != repeat.Id), "Completed schedule is not picked up again");
    }
}
