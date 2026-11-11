using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Framework.Communication;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Framework.Email;

public sealed class EmailAttachment
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = [];
}

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
    public string? MessageId { get; set; }
    public string? InReplyTo { get; set; }
    public string? References { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public List<EmailAttachment> Attachments { get; set; } = [];
    public string? ReferenceDocType { get; set; }
    public string? ReferenceName { get; set; }
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
    public string FromAddress { get; set; } = "noreply@frogframe.local";
    public string? FromDisplayName { get; set; } = "FrogFrame";
}

public sealed class SmtpEmailSender(SmtpEmailOptions options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (message.To.Count == 0)
            throw new ArgumentException("Email must have at least one recipient in 'To'.", nameof(message));

        var mime = new MimeMessage();

        var fromAddr = !string.IsNullOrWhiteSpace(message.From) ? message.From : options.FromAddress;
        mime.From.Add(new MailboxAddress(options.FromDisplayName ?? "", fromAddr));

        foreach (var to in message.To)
        {
            if (string.IsNullOrWhiteSpace(to)) continue;
            if (MailboxAddress.TryParse(to.Trim(), out var parsedTo))
                mime.To.Add(parsedTo);
            else
                mime.To.Add(new MailboxAddress("", to.Trim()));
        }

        foreach (var cc in message.Cc)
        {
            if (string.IsNullOrWhiteSpace(cc)) continue;
            if (MailboxAddress.TryParse(cc.Trim(), out var parsedCc))
                mime.Cc.Add(parsedCc);
            else
                mime.Cc.Add(new MailboxAddress("", cc.Trim()));
        }

        foreach (var bcc in message.Bcc)
        {
            if (string.IsNullOrWhiteSpace(bcc)) continue;
            if (MailboxAddress.TryParse(bcc.Trim(), out var parsedBcc))
                mime.Bcc.Add(parsedBcc);
            else
                mime.Bcc.Add(new MailboxAddress("", bcc.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            if (MailboxAddress.TryParse(message.ReplyTo.Trim(), out var parsedReplyTo))
                mime.ReplyTo.Add(parsedReplyTo);
            else
                mime.ReplyTo.Add(new MailboxAddress("", message.ReplyTo.Trim()));
        }

        mime.Subject = message.Subject;

        if (!string.IsNullOrWhiteSpace(message.MessageId))
            mime.MessageId = message.MessageId.Trim('<', '>');

        if (!string.IsNullOrWhiteSpace(message.InReplyTo))
            mime.InReplyTo = message.InReplyTo.Trim('<', '>');

        if (!string.IsNullOrWhiteSpace(message.References))
        {
            var refs = message.References.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var r in refs)
                mime.References.Add(r.Trim('<', '>'));
        }

        foreach (var (headerName, headerVal) in message.Headers)
            mime.Headers.Add(headerName, headerVal);

        var builder = new BodyBuilder();
        if (!string.IsNullOrWhiteSpace(message.BodyText))
            builder.TextBody = message.BodyText;
        if (!string.IsNullOrWhiteSpace(message.BodyHtml))
            builder.HtmlBody = message.BodyHtml;

        foreach (var att in message.Attachments)
        {
            var cType = ContentType.Parse(string.IsNullOrWhiteSpace(att.ContentType) ? "application/octet-stream" : att.ContentType);
            builder.Attachments.Add(att.FileName, att.Content, cType);
        }

        mime.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var secureOptions = options.EnableSsl
            ? SecureSocketOptions.Auto
            : SecureSocketOptions.StartTlsWhenAvailable;

        await client.ConnectAsync(options.Host, options.Port, secureOptions, ct);

        if (!string.IsNullOrEmpty(options.UserName))
        {
            await client.AuthenticateAsync(options.UserName, options.Password ?? "", ct);
        }

        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
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
        var msgId = !string.IsNullOrWhiteSpace(message.MessageId)
            ? message.MessageId
            : $"<{Guid.NewGuid():N}@frogframe.local>";

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
            ["message_id"] = msgId,
            ["in_reply_to"] = message.InReplyTo,
            ["references"] = message.References,
            ["reference_doctype"] = message.ReferenceDocType,
            ["reference_name"] = message.ReferenceName,
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

public sealed class OutboxEmailHandler(
    IEmailSender sender,
    ICommunicationStore? communicationStore = null,
    TimeProvider? clock = null) : IEventHandler
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
        var messageId = message.Payload.GetValueOrDefault("message_id") ?? $"<{Guid.NewGuid():N}@frogframe.local>";
        var inReplyTo = message.Payload.GetValueOrDefault("in_reply_to");
        var references = message.Payload.GetValueOrDefault("references");
        var refDocType = message.Payload.GetValueOrDefault("reference_doctype");
        var refDocName = message.Payload.GetValueOrDefault("reference_name");

        var email = new EmailMessage
        {
            To = to.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Subject = subject,
            BodyHtml = bodyHtml,
            BodyText = bodyText,
            From = from,
            ReplyTo = replyTo,
            MessageId = messageId,
            InReplyTo = inReplyTo,
            References = references,
            ReferenceDocType = refDocType,
            ReferenceName = refDocName
        };

        if (!string.IsNullOrWhiteSpace(cc))
            email.Cc = cc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        if (!string.IsNullOrWhiteSpace(bcc))
            email.Bcc = bcc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        await sender.SendAsync(email, ct);

        // Record outbound communication after successful send
        if (communicationStore is not null && !string.IsNullOrWhiteSpace(refDocType) && !string.IsNullOrWhiteSpace(refDocName))
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            var comm = new CommunicationEntry
            {
                Id = Guid.NewGuid(),
                TenantId = message.TenantId,
                ReferenceDocType = refDocType,
                ReferenceName = refDocName,
                Direction = CommunicationDirection.Out,
                MessageId = messageId,
                InReplyTo = inReplyTo,
                Subject = subject,
                Sender = from ?? "noreply@frogframe.local",
                Recipients = email.To,
                Cc = email.Cc,
                // Rendered as markup in the document timeline: sanitize like inbound email.
                ContentHtml = HtmlContentSanitizer.Sanitize(bodyHtml),
                ContentText = bodyText,
                OccurredAt = now,
                CreatedAt = now
            };
            await communicationStore.SaveAsync(comm, ct);
        }
    }
}
