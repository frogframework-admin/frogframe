using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Framework.Notifications;

public sealed class NotificationRuleEventHandler(
    NotificationRuleEngine engine,
    IDocumentStore store,
    IMetadataProvider metadata) : IEventHandler
{
    public bool CanHandle(string eventType) =>
        eventType is "document.inserted" or "document.updated" or "workflow.transitioned" or "document.submit";

    public async Task HandleAsync(OutboxMessage m, CancellationToken ct)
    {
        var p = m.Payload;
        var docType = p.GetValueOrDefault("docType");
        var name = p.GetValueOrDefault("name");
        if (string.IsNullOrEmpty(docType) || string.IsNullOrEmpty(name)) return;

        var doc = await store.GetAsync(m.TenantId, docType, name, ct);
        if (doc is null) return;

        var actor = p.GetValueOrDefault("actor");

        if (m.Type == "document.inserted")
        {
            await engine.EvaluateAndDispatchRulesAsync(m.TenantId, docType, doc, previous: null, NotificationTrigger.OnCreate, actor, ct);
        }
        else if (m.Type == "document.updated")
        {
            var audits = await store.GetAuditAsync(m.TenantId, docType, name, ct);
            var latestAudit = audits.FirstOrDefault();
            var previous = doc.Clone();
            if (latestAudit is not null)
            {
                metadata.Current.TryGet(docType, out var meta);
                foreach (var ch in latestAudit.Changes)
                {
                    var field = meta?.Fields.FirstOrDefault(f =>
                        string.Equals(f.Label, ch.Field, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(f.Name, ch.Field, StringComparison.OrdinalIgnoreCase));
                    var fieldName = field?.Name ?? ch.Field;
                    previous.Data[fieldName] = ch.Old;
                    previous.Data[ch.Field] = ch.Old;
                }
            }
            await engine.EvaluateAndDispatchRulesAsync(m.TenantId, docType, doc, previous, NotificationTrigger.OnValueChange, actor, ct);
        }
        else if (m.Type is "workflow.transitioned" or "document.submit")
        {
            var isSubmit = m.Type == "document.submit"
                || p.GetValueOrDefault("from") == "Draft"
                || string.Equals(p.GetValueOrDefault("action"), "Submit", StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.GetValueOrDefault("action"), "Ajukan", StringComparison.OrdinalIgnoreCase)
                || p.GetValueOrDefault("to") == "Submitted";

            if (isSubmit)
            {
                await engine.EvaluateAndDispatchRulesAsync(m.TenantId, docType, doc, previous: null, NotificationTrigger.OnSubmit, actor, ct);
            }
        }
    }
}

public sealed class NotificationRuleDateJob(
    INotificationRuleStore ruleStore,
    NotificationRuleEngine engine,
    IDocumentStore store,
    TimeProvider clock) : IScheduledJob
{
    public string Name => "notification-rules-date-trigger";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var tenantId = context.TenantId;
        var rules = await ruleStore.ListAsync(tenantId, docType: null, onlyActive: true, ct: ct);
        var dateRules = rules.Where(r => r.Trigger is NotificationTrigger.DaysBeforeDate or NotificationTrigger.DaysAfterDate).ToList();
        if (dateRules.Count == 0) return;

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

        foreach (var rule in dateRules)
        {
            if (string.IsNullOrWhiteSpace(rule.TriggerField)) continue;
            var days = rule.DaysOffset ?? 0;
            var targetDate = rule.Trigger == NotificationTrigger.DaysBeforeDate
                ? today.AddDays(days)
                : today.AddDays(-days);

            var targetDateStr = targetDate.ToString("yyyy-MM-dd");

            var query = new StoreQuery(
                TenantId: tenantId,
                DocType: rule.DocType,
                Scope: RowScope.All,
                Filters: [new FilterClause(rule.TriggerField, "=", targetDateStr)],
                Search: null,
                SearchFields: [],
                Sort: [],
                Skip: 0,
                Take: 100);

            var res = await store.QueryAsync(query, ct);

            foreach (var doc in res.Items)
            {
                if (doc.DocStatus == DocStatus.Cancelled) continue;
                await engine.EvaluateAndDispatchRulesAsync(tenantId, rule.DocType, doc, previous: null, rule.Trigger, "scheduler", ct);
            }
        }
    }
}
