using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Email;
using Framework.Expressions;
using Framework.Persistence;
using Framework.Security;
using Framework.Validation;

namespace Framework.Notifications;

public sealed class NotificationRuleEngine(
    INotificationRuleStore ruleStore,
    IUserDirectory users,
    INotificationStore inbox,
    IEmailQueue emailQueue,
    DocumentEvaluator evaluator,
    FunctionRegistry functions,
    TimeProvider clock)
{
    public async Task<int> EvaluateAndDispatchRulesAsync(
        string tenantId,
        string docType,
        Document current,
        Document? previous,
        NotificationTrigger trigger,
        string? eventActor = null,
        CancellationToken ct = default)
    {
        var rules = await ruleStore.ListAsync(tenantId, docType, onlyActive: true, ct);
        var matchingRules = rules.Where(r => r.Trigger == trigger).ToList();
        if (matchingRules.Count == 0) return 0;

        var tenantUsers = await users.ListAsync(tenantId, ct);
        var dispatchedCount = 0;

        foreach (var rule in matchingRules)
        {
            // 1. Verifikasi OnValueChange
            if (rule.Trigger == NotificationTrigger.OnValueChange)
            {
                if (string.IsNullOrWhiteSpace(rule.TriggerField)) continue;
                var oldVal = previous?.Data.GetValueOrDefault(rule.TriggerField);
                var newVal = current.Data.GetValueOrDefault(rule.TriggerField);
                if (string.Equals(oldVal?.ToString(), newVal?.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
            }

            // 2. Evaluasi Kondisi Ekspresi
            if (!string.IsNullOrWhiteSpace(rule.Condition))
            {
                try
                {
                    var parser = new ExpressionParser(functions);
                    var parsed = parser.Parse(rule.Condition);
                    var actorUser = tenantUsers.FirstOrDefault(u => u.Id == eventActor)
                                    ?? new UserContext(eventActor ?? "system", "System", tenantId, new HashSet<string> { "System Manager" }, new Dictionary<string, string>());

                    var condPassed = evaluator.Bool(parsed, fallback: false, current.Data, actorUser);
                    if (!condPassed) continue;
                }
                catch
                {
                    // Jika ekspresi gagal dievaluasi, lewati rule ini
                    continue;
                }
            }

            // 3. Render Template Subjek dan Pesan
            var mergeData = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in current.Data)
            {
                mergeData[k] = v?.ToString();
            }
            mergeData["name"] = current.Name;
            mergeData["doctype"] = current.DocType;
            mergeData["owner"] = current.Owner;
            mergeData["docstatus"] = current.DocStatus switch
            {
                DocStatus.Draft => "Draft",
                DocStatus.Submitted => "Submitted",
                DocStatus.Cancelled => "Cancelled",
                _ => current.DocStatus.ToString()
            };
            mergeData["actor"] = eventActor ?? current.ModifiedBy ?? current.Owner;

            var subject = EmailTemplateEngine.Render(rule.SubjectTemplate, mergeData);
            var body = EmailTemplateEngine.Render(rule.MessageTemplate, mergeData);

            // 4. Resolusi Penerima
            var recipients = ResolveRecipients(rule, current, tenantUsers);
            if (recipients.Count == 0) continue;

            // 5. Dispatch Kanal (InApp / Email)
            foreach (var recipient in recipients)
            {
                var idempotencyBase = $"{tenantId}:notifrule:{rule.Id}:{current.Name}:v{current.Version}:{recipient.UserId}";

                if (rule.Channel is NotificationChannel.InApp or NotificationChannel.Both)
                {
                    var notifId = new Guid(MD5.HashData(Encoding.UTF8.GetBytes(idempotencyBase)));
                    inbox.Add(new Notification(
                        notifId,
                        tenantId,
                        recipient.UserId,
                        subject,
                        body,
                        current.DocType,
                        current.Name,
                        clock.GetUtcNow()
                    ));
                    dispatchedCount++;
                }

                if (rule.Channel is NotificationChannel.Email or NotificationChannel.Both)
                {
                    if (!string.IsNullOrWhiteSpace(recipient.Email))
                    {
                        var emailMsg = new EmailMessage
                        {
                            To = [recipient.Email],
                            Subject = subject,
                            BodyHtml = body,
                            BodyText = body
                        };
                        await emailQueue.QueueAsync(tenantId, emailMsg, idempotencyKey: $"{idempotencyBase}:email", ct);
                        dispatchedCount++;
                    }
                }
            }
        }

        return dispatchedCount;
    }

    private static List<NotificationRecipient> ResolveRecipients(
        NotificationRule rule,
        Document doc,
        IReadOnlyList<UserContext> tenantUsers)
    {
        var result = new List<NotificationRecipient>();

        switch (rule.ReceiverType)
        {
            case NotificationReceiverType.Role:
                if (!string.IsNullOrWhiteSpace(rule.ReceiverValue))
                {
                    foreach (var u in tenantUsers.Where(u => u.Roles.Contains(rule.ReceiverValue.Trim())))
                    {
                        var email = u.Attributes.TryGetValue("email", out var em) ? em?.ToString() : null;
                        result.Add(new NotificationRecipient(u.Id, email));
                    }
                }
                break;

            case NotificationReceiverType.Owner:
                if (!string.IsNullOrEmpty(doc.Owner))
                {
                    var ownerUser = tenantUsers.FirstOrDefault(u => u.Id == doc.Owner);
                    var email = ownerUser?.Attributes.TryGetValue("email", out var em) == true ? em?.ToString() : null;
                    result.Add(new NotificationRecipient(doc.Owner, email));
                }
                break;

            case NotificationReceiverType.SpecificUser:
                if (!string.IsNullOrWhiteSpace(rule.ReceiverValue))
                {
                    var u = tenantUsers.FirstOrDefault(u => u.Id == rule.ReceiverValue.Trim());
                    if (u is not null)
                    {
                        var email = u.Attributes.TryGetValue("email", out var em) ? em?.ToString() : null;
                        result.Add(new NotificationRecipient(u.Id, email));
                    }
                }
                break;

            case NotificationReceiverType.Field:
                if (!string.IsNullOrWhiteSpace(rule.ReceiverValue) &&
                    doc.Data.TryGetValue(rule.ReceiverValue.Trim(), out var fVal) && fVal is not null)
                {
                    var strVal = fVal.ToString()!.Trim();
                    if (strVal.Contains('@'))
                    {
                        // Nilai field adalah alamat email langsung
                        result.Add(new NotificationRecipient(strVal, strVal));
                    }
                    else
                    {
                        // Nilai field diasumsikan user_id
                        var u = tenantUsers.FirstOrDefault(u => u.Id == strVal);
                        var email = u?.Attributes.TryGetValue("email", out var em) == true ? em?.ToString() : null;
                        result.Add(new NotificationRecipient(strVal, email));
                    }
                }
                break;
        }

        return result;
    }

    public record NotificationRecipient(string UserId, string? Email);
}
