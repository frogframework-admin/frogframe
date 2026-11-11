using System.Text.RegularExpressions;
using Framework.Documents;
using Framework.Files;
using Framework.Metadata;
using Framework.Security;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Framework.Communication;

public sealed class InboundEmailAttachment
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = [];
}

public sealed class InboundEmailMessage
{
    public long Uid { get; set; }
    public string MessageId { get; set; } = "";
    public string? InReplyTo { get; set; }
    public List<string> References { get; set; } = [];
    public string Subject { get; set; } = "";
    public string From { get; set; } = "";
    public List<string> To { get; set; } = [];
    public List<string> Cc { get; set; } = [];
    public string? BodyHtml { get; set; }
    public string? BodyText { get; set; }
    public DateTimeOffset Date { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public List<InboundEmailAttachment> Attachments { get; set; } = [];
    public bool IsAutoSubmitted { get; set; }
}

public sealed record MailboxFolderInfo(long UidValidity, int MessageCount);

public interface IMailboxReader
{
    Task<MailboxFolderInfo> OpenFolderAsync(EmailAccount account, string password, CancellationToken ct = default);
    Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(EmailAccount account, string password, long lastUid, int batchSize = 100, CancellationToken ct = default);
}

public sealed class MailKitMailboxReader : IMailboxReader
{
    public async Task<MailboxFolderInfo> OpenFolderAsync(EmailAccount account, string password, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        var secureOptions = account.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(account.ImapHost, account.ImapPort, secureOptions, ct);
        await client.AuthenticateAsync(account.Username, password, ct);

        var folderName = string.IsNullOrWhiteSpace(account.Folder) ? "INBOX" : account.Folder;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var info = new MailboxFolderInfo(folder.UidValidity, folder.Count);
        await client.DisconnectAsync(true, ct);
        return info;
    }

    public async Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(
        EmailAccount account,
        string password,
        long lastUid,
        int batchSize = 100,
        CancellationToken ct = default)
    {
        using var client = new ImapClient();
        var secureOptions = account.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(account.ImapHost, account.ImapPort, secureOptions, ct);
        await client.AuthenticateAsync(account.Username, password, ct);

        var folderName = string.IsNullOrWhiteSpace(account.Folder) ? "INBOX" : account.Folder;
        var folder = await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var allUids = await folder.SearchAsync(SearchQuery.All, ct);
        var targetUids = allUids
            .Where(u => u.Id > lastUid)
            .OrderBy(u => u.Id)
            .Take(batchSize)
            .ToList();

        var result = new List<InboundEmailMessage>();
        foreach (var uid in targetUids)
        {
            var mime = await folder.GetMessageAsync(uid, ct);
            result.Add(ConvertMimeMessage(uid.Id, mime));
        }

        await client.DisconnectAsync(true, ct);
        return result;
    }

    public static InboundEmailMessage ConvertMimeMessage(long uid, MimeMessage mime)
    {
        var msg = new InboundEmailMessage
        {
            Uid = uid,
            MessageId = !string.IsNullOrWhiteSpace(mime.MessageId) ? mime.MessageId.Trim() : $"<{Guid.NewGuid():N}@incoming.frogframe>",
            InReplyTo = mime.InReplyTo?.Trim(),
            Subject = mime.Subject ?? "",
            From = mime.From.Mailboxes.FirstOrDefault()?.Address ?? mime.From.ToString(),
            To = mime.To.Mailboxes.Select(m => m.Address).ToList(),
            Cc = mime.Cc.Mailboxes.Select(m => m.Address).ToList(),
            BodyHtml = mime.HtmlBody,
            BodyText = mime.TextBody,
            Date = mime.Date != default ? mime.Date : DateTimeOffset.UtcNow
        };

        if (mime.References is not null)
        {
            foreach (var r in mime.References)
                if (!string.IsNullOrWhiteSpace(r)) msg.References.Add(r.Trim());
        }

        foreach (var header in mime.Headers)
            msg.Headers[header.Field] = header.Value;

        // Auto-responder detection
        var autoSubmitted = mime.Headers["Auto-Submitted"];
        if (!string.IsNullOrWhiteSpace(autoSubmitted) && !autoSubmitted.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
            msg.IsAutoSubmitted = true;

        if (mime.Headers.Contains("X-Auto-Response-Suppress"))
            msg.IsAutoSubmitted = true;

        var prec = mime.Headers["Precedence"];
        if (!string.IsNullOrWhiteSpace(prec) && (prec.Equals("bulk", StringComparison.OrdinalIgnoreCase) ||
                                                prec.Equals("auto_reply", StringComparison.OrdinalIgnoreCase) ||
                                                prec.Equals("junk", StringComparison.OrdinalIgnoreCase) ||
                                                prec.Equals("list", StringComparison.OrdinalIgnoreCase)))
        {
            msg.IsAutoSubmitted = true;
        }

        // Attachments
        foreach (var att in mime.Attachments)
        {
            if (att is MimePart part)
            {
                using var ms = new MemoryStream();
                part.Content?.DecodeTo(ms);
                msg.Attachments.Add(new InboundEmailAttachment
                {
                    FileName = part.FileName ?? "attachment",
                    ContentType = part.ContentType?.MimeType ?? "application/octet-stream",
                    Content = ms.ToArray()
                });
            }
        }

        return msg;
    }
}

public sealed class InboundEmailProcessor(
    IEmailAccountStore accountStore,
    ICommunicationStore communicationStore,
    IMailboxReader mailboxReader,
    ISecretProtector secretProtector,
    IAutomationIdentity automationIdentity,
    DocumentService docs,
    IMetadataProvider metadata,
    TimeProvider clock,
    AttachmentService? attachmentService = null,
    ILogger<InboundEmailProcessor>? logger = null)
{
    private static readonly Regex SubjectDocNameRegex = new(@"\[#([a-zA-Z0-9_\-\./]+)\]", RegexOptions.Compiled);
    private readonly ILogger _logger = logger ?? NullLogger<InboundEmailProcessor>.Instance;

    public const long MaxEmailSizeBytes = 25 * 1024 * 1024; // 25 MB

    public async Task<int> ProcessAccountAsync(EmailAccount account, CancellationToken ct = default)
    {
        var password = secretProtector.Unprotect(account.PasswordEnc);

        MailboxFolderInfo folderInfo;
        try
        {
            folderInfo = await mailboxReader.OpenFolderAsync(account, password, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to IMAP folder for account {AccountName} ({AccountId})", account.Name, account.Id);
            await accountStore.UpdatePollingStateAsync(account.Id, account.UidValidity, account.LastUid, clock.GetUtcNow(), ex.Message, ct);
            throw;
        }

        var lastUid = account.LastUid;
        var uidValidity = folderInfo.UidValidity;

        // Handle UIDVALIDITY change: if server invalidated UIDs, reset to 0 to re-read all and rely on Message-ID deduplication
        if (account.UidValidity > 0 && uidValidity != account.UidValidity)
        {
            _logger.LogWarning("UIDVALIDITY changed from {Old} to {New} for account {AccountName}. Resetting last_uid to 0.",
                account.UidValidity, uidValidity, account.Name);
            lastUid = 0;
        }

        var messages = await mailboxReader.FetchNewMessagesAsync(account, password, lastUid, batchSize: 100, ct);
        var processedCount = 0;

        foreach (var msg in messages)
        {
            if (ct.IsCancellationRequested) break;

            lastUid = Math.Max(lastUid, msg.Uid);

            // 1. Skip auto-responders / bulk mail to prevent mail loops
            if (msg.IsAutoSubmitted)
            {
                _logger.LogInformation("Skipping auto-submitted message {MessageId} from {Sender}", msg.MessageId, msg.From);
                await accountStore.UpdatePollingStateAsync(account.Id, uidValidity, lastUid, clock.GetUtcNow(), null, ct);
                continue;
            }

            // 2. Deduplication on (tenant_id, message_id)
            if (await communicationStore.ExistsMessageIdAsync(account.TenantId, msg.MessageId, ct))
            {
                await accountStore.UpdatePollingStateAsync(account.Id, uidValidity, lastUid, clock.GetUtcNow(), null, ct);
                continue;
            }

            // 3. Message size validation
            var totalAttachmentBytes = msg.Attachments.Sum(a => (long)a.Content.Length);
            var estimatedTotalBytes = (msg.BodyHtml?.Length ?? 0) + (msg.BodyText?.Length ?? 0) + totalAttachmentBytes;
            if (estimatedTotalBytes > MaxEmailSizeBytes)
            {
                _logger.LogWarning("Email {MessageId} exceeds max size ({Estimated} bytes). Storing header only.", msg.MessageId, estimatedTotalBytes);
                msg.BodyHtml = "<p><em>[Message body and attachments omitted: size exceeded 25MB]</em></p>";
                msg.BodyText = "[Message body and attachments omitted: size exceeded 25MB]";
                msg.Attachments.Clear();
            }

            // 4. HTML sanitization
            string? cleanHtml = null;
            if (!string.IsNullOrWhiteSpace(msg.BodyHtml))
            {
                cleanHtml = HtmlContentSanitizer.Sanitize(msg.BodyHtml);
            }

            // 5. Thread / Document matching
            string? refDocType = null;
            string? refDocName = null;

            // Step 5.1: In-Reply-To and References header matching
            var threadMessageIds = new List<string>();
            if (!string.IsNullOrWhiteSpace(msg.InReplyTo))
                threadMessageIds.Add(msg.InReplyTo.Trim());
            foreach (var r in msg.References)
                if (!string.IsNullOrWhiteSpace(r)) threadMessageIds.Add(r.Trim());

            foreach (var tId in threadMessageIds)
            {
                var existingComm = await communicationStore.GetByMessageIdAsync(account.TenantId, tId, ct);
                if (existingComm is not null && !string.IsNullOrWhiteSpace(existingComm.ReferenceDocType) && !string.IsNullOrWhiteSpace(existingComm.ReferenceName))
                {
                    refDocType = existingComm.ReferenceDocType;
                    refDocName = existingComm.ReferenceName;
                    break;
                }
            }

            // Step 5.2: Subject tag matching [#DOC-NAME]
            if (refDocType is null && !string.IsNullOrWhiteSpace(msg.Subject))
            {
                var match = SubjectDocNameRegex.Match(msg.Subject);
                if (match.Success)
                {
                    var docNameCandidate = match.Groups[1].Value.Trim();
                    // First check if target AppendToDocType has this doc
                    if (!string.IsNullOrWhiteSpace(account.AppendToDocType))
                    {
                        try
                        {
                            await docs.GetAsync(UserContext.System(account.TenantId), account.AppendToDocType, docNameCandidate, ct);
                            refDocType = account.AppendToDocType;
                            refDocName = docNameCandidate;
                        }
                        catch (DocumentNotFoundException)
                        {
                        }
                    }

                    // Otherwise check across registered DocTypes
                    if (refDocType is null)
                    {
                        foreach (var (dtName, dt) in metadata.Current.DocTypes)
                        {
                            if (dt.Def.IsChild) continue;
                            try
                            {
                                await docs.GetAsync(UserContext.System(account.TenantId), dtName, docNameCandidate, ct);
                                refDocType = dtName;
                                refDocName = docNameCandidate;
                                break;
                            }
                            catch (DocumentNotFoundException)
                            {
                            }
                        }
                    }
                }
            }

            // Step 5.3: Append to DocType (create new document if unthreaded)
            if (refDocType is null && !string.IsNullOrWhiteSpace(account.AppendToDocType))
            {
                var serviceUser = await automationIdentity.ForUserAsync(account.TenantId, account.ServiceUserId);
                var meta = metadata.Current.DocTypes.TryGetValue(account.AppendToDocType, out var m) && !m.Def.IsChild
                    ? m : throw new DocumentNotFoundException("DocType", account.AppendToDocType);

                var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                foreach (var (docField, sourceProp) in account.FieldMap)
                {
                    payload[docField] = sourceProp.ToLowerInvariant() switch
                    {
                        "subject" => msg.Subject,
                        "sender" or "from" => msg.From,
                        "content_text" or "text" => msg.BodyText ?? "",
                        "content_html" or "html" => cleanHtml ?? msg.BodyText ?? "",
                        "date" => msg.Date.ToString("o"),
                        _ => null
                    };
                }

                // Ensure default subject / title if field exists and not mapped
                if (!payload.ContainsKey("title") && !payload.ContainsKey("subject"))
                {
                    if (meta.Def.Fields.Any(f => f.Name.Equals("title", StringComparison.OrdinalIgnoreCase)))
                        payload["title"] = msg.Subject;
                    else if (meta.Def.Fields.Any(f => f.Name.Equals("subject", StringComparison.OrdinalIgnoreCase)))
                        payload["subject"] = msg.Subject;
                }

                var insertedDoc = await docs.InsertAsync(serviceUser, account.AppendToDocType, payload, ct);
                refDocType = account.AppendToDocType;
                refDocName = insertedDoc.Name;

                // Save safe attachments if AttachmentService is registered
                if (attachmentService is not null && msg.Attachments.Count > 0)
                {
                    foreach (var att in msg.Attachments)
                    {
                        var sniffed = AttachmentService.SniffContentType(att.Content, att.FileName);
                        if (sniffed is not null)
                        {
                            try
                            {
                                using var stream = new MemoryStream(att.Content);
                                await attachmentService.UploadAsync(serviceUser, refDocType, refDocName, att.FileName, stream, isPrivate: true, ct);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to upload attachment {FileName} for new doc {DocName}", att.FileName, refDocName);
                            }
                        }
                    }
                }
            }

            // Step 5.4: Save CommunicationEntry
            var entry = new CommunicationEntry
            {
                Id = Guid.NewGuid(),
                TenantId = account.TenantId,
                ReferenceDocType = refDocType,
                ReferenceName = refDocName,
                Direction = CommunicationDirection.In,
                MessageId = msg.MessageId,
                InReplyTo = msg.InReplyTo,
                Subject = msg.Subject,
                Sender = msg.From,
                Recipients = msg.To,
                Cc = msg.Cc,
                ContentHtml = cleanHtml,
                ContentText = msg.BodyText,
                EmailAccountId = account.Id,
                OccurredAt = msg.Date,
                CreatedAt = clock.GetUtcNow()
            };

            await communicationStore.SaveAsync(entry, ct);
            processedCount++;

            // Update last_uid after each message successfully processed to ensure restart resilience
            await accountStore.UpdatePollingStateAsync(account.Id, uidValidity, lastUid, clock.GetUtcNow(), null, ct);
        }

        // Final poll state update
        await accountStore.UpdatePollingStateAsync(account.Id, uidValidity, lastUid, clock.GetUtcNow(), null, ct);
        return processedCount;
    }
}

public sealed class EmailAccountPollJob : Framework.Jobs.IScheduledJob
{
    public string Name => "EmailAccountPollJob";
    public string Schedule => "@every_5m";

    public async Task ExecuteAsync(Framework.Jobs.ScheduledJobContext context, CancellationToken ct)
    {
        var accountStore = (IEmailAccountStore)(context.Services.GetService(typeof(IEmailAccountStore))
            ?? throw new InvalidOperationException("IEmailAccountStore is not registered."));
        var processor = (InboundEmailProcessor)(context.Services.GetService(typeof(InboundEmailProcessor))
            ?? throw new InvalidOperationException("InboundEmailProcessor is not registered."));

        context.Logger.LogJobStarting(Name, context.TenantId, context.ScheduledTime);
        try
        {
            var accounts = await accountStore.ListByTenantAsync(context.TenantId, ct);
            var totalProcessed = 0;
            foreach (var account in accounts)
            {
                if (ct.IsCancellationRequested) break;
                if (!account.IsActive) continue;
                try
                {
                    var count = await processor.ProcessAccountAsync(account, ct);
                    totalProcessed += count;
                }
                catch (Exception ex)
                {
                    context.Logger.LogJobFailed(ex, $"{Name}:{account.Name}", context.TenantId);
                }
            }
            context.Logger.LogJobCompleted($"{Name}: Processed {totalProcessed} message(s)", context.TenantId);
        }
        catch (Exception ex)
        {
            context.Logger.LogJobFailed(ex, Name, context.TenantId);
            throw;
        }
    }
}
