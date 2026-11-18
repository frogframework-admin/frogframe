using System.Globalization;
using Apps.Erp.Hooks;
using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Apps.Erp.Ess;

/// <summary>
/// Runs every 5 minutes: aggregates unprocessed mobile punches into one Attendance document per employee per work date
/// (check-in = earliest counted in-punch, check-out = latest counted out-punch, coordinates, selfie photos, and flagged punch notes).
/// Attendance documents are persisted through <see cref="DocumentService"/> so tardiness hooks, payroll period locks, and audit trails apply.
/// Validation errors (e.g., payroll period already locked) are recorded on the punch (<c>process_error</c>) for HR review.
/// </summary>
public sealed class EssAttendanceAggregationJob : IScheduledJob
{
    public string Name => "Erp.EssAttendanceAggregation";
    public string Schedule => "@every_5m";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var punches = context.Services.GetRequiredService<IEssPunchStore>();
        var store = context.Services.GetRequiredService<IDocumentStore>();
        var config = context.Services.GetService<IConfiguration>();
        var zone = EssAttendanceService.ResolveTimeZone(config?["Ess:TimeZone"]);
        var batch = Math.Clamp(config?.GetValue("Ess:AggregationBatchSize", 1000) ?? 1000, 1, 20000);
        var sys = new SystemDocs(context.Docs, context.TenantId);
        var clock = context.Services.GetService<TimeProvider>() ?? TimeProvider.System;

        var pending = await punches.PendingDaysAsync(context.TenantId, batch, ct);
        int created = 0, updated = 0, failed = 0;
        foreach (var (employee, workDate) in pending)
        {
            ct.ThrowIfCancellationRequested();
            var day = await punches.ListDayAsync(context.TenantId, employee, workDate, ct);
            var counted = day.Where(p => p.Counts).OrderBy(p => p.PunchTime).ToList();
            var ids = day.Where(p => p.ProcessedAt is null && p.Counts).Select(p => p.Id).ToList();
            if (counted.Count == 0) { await punches.MarkProcessedAsync(context.TenantId, ids, null, null, clock.GetUtcNow(), ct); continue; }
            try
            {
                var (name, isNew) = await UpsertAttendanceAsync(store, sys, context.TenantId, employee, workDate, counted, zone, ct);
                if (isNew) created++; else updated++;
                await punches.MarkProcessedAsync(context.TenantId, ids, name, null, clock.GetUtcNow(), ct);
            }
            catch (ValidationFailedException ex)
            {
                // Cannot succeed without HR intervention (e.g., payslip for that month is already submitted): record error and do not retry endlessly.
                failed++;
                var message = string.Join("; ", ex.Errors.Select(e => e.Message));
                await punches.MarkProcessedAsync(context.TenantId, ids, null, message.Length > 1000 ? message[..1000] : message, clock.GetUtcNow(), ct);
            }
            catch (ConcurrencyException)
            {
                // Document is concurrently being edited by HR/another worker: leave pending and retry on the next cycle.
            }
        }
        if (pending.Count > 0)
            context.Services.GetService<ILoggerFactory>()?.CreateLogger<EssAttendanceAggregationJob>()
                .LogInformation("ESS attendance: {Days} employee-days, {Created} created, {Updated} updated, {Failed} failed",
                    pending.Count, created, updated, failed);
    }

    internal static async Task<(string Name, bool Created)> UpsertAttendanceAsync(IDocumentStore store, SystemDocs sys, string tenantId,
        string employee, DateOnly workDate, IReadOnlyList<EssPunch> counted, TimeZoneInfo zone, CancellationToken ct)
    {
        var firstIn = counted.FirstOrDefault(p => p.PunchType == "in");
        var lastOut = counted.LastOrDefault(p => p.PunchType == "out" && (firstIn is null || p.PunchTime > firstIn.PunchTime));
        var flags = counted.Where(p => p.Status == EssPunchStatus.Flagged)
            .Select(p => $"{(p.PunchType == "in" ? "Check-in" : "Check-out")} {TimeZoneInfo.ConvertTime(p.PunchTime, zone):HH:mm}: " +
                         string.Join(", ", p.Reasons.Select(EssReason.Message)))
            .ToList();

        var m = new Masters(store, tenantId);
        var existing = (await m.QueryAsync("Attendance",
                [new FilterClause("employee", "=", employee), new FilterClause("attendance_date", "=", D.Iso(workDate))], ct, take: 1))
            .FirstOrDefault();

        var patch = new Dictionary<string, object?>();
        string? Utc(DateTimeOffset t) => t.ToUniversalTime().ToString(DatetimeFieldType.Format, CultureInfo.InvariantCulture);
        string? Geo(EssPunch p) => p.Latitude is { } lat && p.Longitude is { } lon
            ? GeolocationFieldType.Format(lat, lon) : null;

        if (existing is null)
        {
            patch["employee"] = employee;
            patch["attendance_date"] = D.Iso(workDate);
            patch["status"] = "Present";
            patch["source"] = "Mobile app";
        }
        var data = existing?.Data;
        // Merge with existing record (e.g., manual HR entry): retain earliest check-in and latest check-out.
        if (firstIn is not null && (data is null || DatetimeFieldType.ToZone(data.GetValueOrDefault("check_in")) is not { } curIn || firstIn.PunchTime < curIn))
        {
            patch["check_in"] = Utc(firstIn.PunchTime);
            patch["check_in_location"] = Geo(firstIn);
            if (firstIn.PhotoFileId is { } inPhoto) patch["check_in_photo"] = inPhoto;
        }
        else if (firstIn?.PhotoFileId is { } laterInPhoto && data?.Str("check_in_photo") is null)
        {
            patch["check_in_photo"] = laterInPhoto;
        }
        if (lastOut is not null && (data is null || DatetimeFieldType.ToZone(data.GetValueOrDefault("check_out")) is not { } curOut || lastOut.PunchTime > curOut))
        {
            var inUtc = patch.GetValueOrDefault("check_in") as string ?? data?.Str("check_in");
            var outUtc = Utc(lastOut.PunchTime);
            if (inUtc is not null && string.CompareOrdinal(outUtc, inUtc) <= 0 && firstIn is not null)
                outUtc = Utc(firstIn.PunchTime.AddSeconds(1));
            patch["check_out"] = outUtc;
            patch["check_out_location"] = Geo(lastOut);
            if (lastOut.PhotoFileId is { } outPhoto) patch["check_out_photo"] = outPhoto;
        }
        else if (lastOut?.PhotoFileId is { } laterOutPhoto && data?.Str("check_out_photo") is null)
        {
            patch["check_out_photo"] = laterOutPhoto;
        }
        var flagText = flags.Count == 0 ? null : string.Join("\n", flags);
        if (flagText != data?.Str("ess_flags")) patch["ess_flags"] = flagText;

        if (existing is null)
            return ((await sys.InsertAsync("Attendance", patch, "ESS mobile attendance", ct)).Name, true);
        if (patch.Count > 0)
            await sys.Service.UpdateAsync(sys.User, "Attendance", existing.Name, existing.Version, patch, ct);
        return (existing.Name, false);
    }
}
