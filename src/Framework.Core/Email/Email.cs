using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Reflection;
using System.Text.RegularExpressions;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;

namespace Framework.Email;

public sealed class EmailMessage
{
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public List<string> Bcc { get; set; } = [];
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string? BodyText { get; set; }
    public string? From { get; set; }
    public string? ReplyTo { get; set; }
}

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed class SmtpEmailOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25;
    public bool EnableSsl { get; set; } = false;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "noreply@docframe.local";
    public string? FromDisplayName { get; set; } = "DocFrame";
}

public sealed class SmtpEmailSender(SmtpEmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (message.To.Count == 0)
            throw new ArgumentException("Email must have at least one recipient in 'To'.", nameof(message));

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl
        };

        if (!string.IsNullOrEmpty(options.UserName))
        {
            client.Credentials = new NetworkCredential(options.UserName, options.Password ?? "");
        }

        using var mail = new MailMessage();
        var fromAddr = !string.IsNullOrWhiteSpace(message.From) ? message.From : options.FromAddress;
        mail.From = new MailAddress(fromAddr, options.FromDisplayName);

        foreach (var to in message.To)
            if (!string.IsNullOrWhiteSpace(to)) mail.To.Add(to.Trim());

        foreach (var cc in message.Cc)
            if (!string.IsNullOrWhiteSpace(cc)) mail.CC.Add(cc.Trim());

        foreach (var bcc in message.Bcc)
            if (!string.IsNullOrWhiteSpace(bcc)) mail.Bcc.Add(bcc.Trim());

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
            mail.ReplyToList.Add(new MailAddress(message.ReplyTo.Trim()));

        mail.Subject = message.Subject;
        mail.IsBodyHtml = true;
        mail.Body = message.BodyHtml;

        if (!string.IsNullOrWhiteSpace(message.BodyText))
        {
            var textView = AlternateView.CreateAlternateViewFromString(message.BodyText, null, "text/plain");
            mail.AlternateViews.Add(textView);
        }

        await client.SendMailAsync(mail, ct);
    }
}

public sealed class InMemoryEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> SentMessages => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public void Clear() => _sent.Clear();
}

public static class EmailTemplateEngine
{
    private static readonly Regex VariableRegex = new(@"\{\{\s*([a-zA-Z0-9_.-]+)\s*\}\}", RegexOptions.Compiled);

    public static string Render(string template, IReadOnlyDictionary<string, string?> variables)
    {
        if (string.IsNullOrEmpty(template)) return "";

        return VariableRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (variables.TryGetValue(key, out var val) && val is not null)
                return val;
            return "";
        });
    }

    public static string Render(string template, object model)
    {
        if (string.IsNullOrEmpty(template) || model is null) return "";

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (model is IDictionary<string, object?> d)
        {
            foreach (var kvp in d)
                dict[kvp.Key] = kvp.Value?.ToString();
        }
        else
        {
            foreach (var prop in model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.CanRead)
                    dict[prop.Name] = prop.GetValue(model)?.ToString();
            }
        }
        return Render(template, dict);
    }
}

public interface IEmailQueue
{
    Task QueueAsync(string tenantId, EmailMessage message, string? idempotencyKey = null, CancellationToken ct = default);
}

public sealed class OutboxEmailQueue(IOutboxStore outbox, TimeProvider clock) : IEmailQueue
{
    public const string EventType = "email.send";

    public async Task QueueAsync(string tenantId, EmailMessage message, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, string?>
        {
            ["to"] = string.Join(";", message.To),
            ["subject"] = message.Subject,
            ["body_html"] = message.BodyHtml,
            ["body_text"] = message.BodyText,
            ["from"] = message.From,
            ["cc"] = message.Cc.Count > 0 ? string.Join(";", message.Cc) : null,
            ["bcc"] = message.Bcc.Count > 0 ? string.Join(";", message.Bcc) : null,
            ["reply_to"] = message.ReplyTo,
        };

        var outboxMessage = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Type = EventType,
            Payload = payload,
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("N"),
            CreatedAt = clock.GetUtcNow()
        };

        await outbox.EnqueueAsync(outboxMessage, ct);
    }
}

public sealed class OutboxEmailHandler(IEmailSender sender) : IEventHandler
{
    public bool CanHandle(string eventType) => eventType == OutboxEmailQueue.EventType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken ct)
    {
        var to = message.Payload.GetValueOrDefault("to");
        if (string.IsNullOrWhiteSpace(to))
            throw new InvalidOperationException("Email 'to' payload missing or empty.");

        var subject = message.Payload.GetValueOrDefault("subject") ?? "";
        var bodyHtml = message.Payload.GetValueOrDefault("body_html") ?? "";
        var bodyText = message.Payload.GetValueOrDefault("body_text");
        var from = message.Payload.GetValueOrDefault("from");
        var cc = message.Payload.GetValueOrDefault("cc");
        var bcc = message.Payload.GetValueOrDefault("bcc");
        var replyTo = message.Payload.GetValueOrDefault("reply_to");

        var email = new EmailMessage
        {
            To = to.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Subject = subject,
            BodyHtml = bodyHtml,
            BodyText = bodyText,
            From = from,
            ReplyTo = replyTo
        };

        if (!string.IsNullOrWhiteSpace(cc))
            email.Cc = cc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (!string.IsNullOrWhiteSpace(bcc))
            email.Bcc = bcc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        await sender.SendAsync(email, ct);
    }
}
