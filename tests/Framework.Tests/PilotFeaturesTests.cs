using System.IO.Compression;
using System.Text;
using Framework.Documents;
using Framework.Email;
using Framework.Files;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    [Test]
    public Task Attachment_sniffing_and_type_detection()
    {
        // 1. PDF
        var pdfBytes = "%PDF-1.7 valid pdf dummy content"u8.ToArray();
        Assert.Equal("application/pdf", AttachmentService.SniffContentType(pdfBytes, "report.pdf"), "sniff PDF");

        // 2. PNG
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };
        Assert.Equal("image/png", AttachmentService.SniffContentType(pngBytes, "image.png"), "sniff PNG");

        // 3. JPEG
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        Assert.Equal("image/jpeg", AttachmentService.SniffContentType(jpegBytes, "photo.jpg"), "sniff JPEG");

        // 4. WebP
        var webpBytes = new byte[16];
        "RIFF"u8.CopyTo(webpBytes.AsSpan(0, 4));
        "WEBP"u8.CopyTo(webpBytes.AsSpan(8, 4));
        Assert.Equal("image/webp", AttachmentService.SniffContentType(webpBytes, "photo.webp"), "sniff WebP");

        // 5. Plain text & CSV
        var csvBytes = "id,name,amount\n1,Widget,100"u8.ToArray();
        Assert.Equal("text/csv", AttachmentService.SniffContentType(csvBytes, "data.csv"), "sniff CSV");

        var txtBytes = "Hello world\nThis is plain text."u8.ToArray();
        Assert.Equal("text/plain", AttachmentService.SniffContentType(txtBytes, "notes.txt"), "sniff TXT");

        // 6. Office .docx (valid zip with word/document.xml)
        using var docxMs = new MemoryStream();
        using (var zip = new ZipArchive(docxMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var entryStream = entry.Open();
            entryStream.Write("<xml/>"u8);
        }
        var docxBytes = docxMs.ToArray();
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            AttachmentService.SniffContentType(docxBytes, "proposal.docx"), "sniff DOCX");

        // 7. Malicious or unsupported files rejected
        // SVG with script
        var svgBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        Assert.True(AttachmentService.SniffContentType(svgBytes, "malicious.svg") is null, "reject SVG");

        // HTML
        var htmlBytes = "<html><body><script>malicious()</script></body></html>"u8.ToArray();
        Assert.True(AttachmentService.SniffContentType(htmlBytes, "page.html") is null, "reject HTML");

        // Windows PE executable
        var exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00 }; // MZ
        Assert.True(AttachmentService.SniffContentType(exeBytes, "program.exe") is null, "reject EXE");

        // CSV containing null byte (potential binary smuggling)
        var dirtyCsv = new byte[] { 0x61, 0x2C, 0x00, 0x62 };
        Assert.True(AttachmentService.SniffContentType(dirtyCsv, "dirty.csv") is null, "reject CSV with null byte");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Attachment_upload_list_download_and_delete()
    {
        var andi = U("andi");
        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["justification"] = "Replacement hardware for engineering team",
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["item_name"] = "Laptop", ["qty"] = 1m, ["rate"] = 15_000_000m }
            }
        });

        var attService = sp.GetRequiredService<AttachmentService>();

        // 1. Upload valid PDF
        var pdfContent = "%PDF-1.4 test procurement quotation attachment content"u8.ToArray();
        using (var ms = new MemoryStream(pdfContent))
        {
            var att = await attService.UploadAsync(andi, PR, pr.Name, "quote.pdf", ms);
            Assert.Equal("quote.pdf", att.FileName, "filename");
            Assert.Equal("application/pdf", att.ContentType, "content type");
            Assert.Equal(pdfContent.Length, att.Size, "size");
            Assert.Equal(andi.TenantId, att.TenantId, "tenant id");
        }

        // 2. Upload valid CSV
        var csvContent = "item,qty,price\nLaptop,1,15000000"u8.ToArray();
        using (var ms = new MemoryStream(csvContent))
        {
            var att2 = await attService.UploadAsync(andi, PR, pr.Name, "spec.csv", ms);
            Assert.Equal("spec.csv", att2.FileName, "filename");
            Assert.Equal("text/csv", att2.ContentType, "csv type");
        }

        // 3. List attachments
        var list = await attService.ListAsync(andi, PR, pr.Name);
        Assert.Equal(2, list.Count, "2 attachments listed");

        // 4. Download content
        var downloaded = await attService.GetFileAsync(andi, list[0].Id);
        Assert.True(downloaded.Content.Length > 0, "content downloaded");

        // 5. Reject invalid file (e.g. executable/fake PDF)
        var fakePdf = "NOT A VALID PDF FILE"u8.ToArray();
        await Assert.Throws<FileRejectedException>(async () =>
        {
            using var ms = new MemoryStream(fakePdf);
            await attService.UploadAsync(andi, PR, pr.Name, "fake.pdf", ms);
        }, "fake pdf rejection");

        // 6. Delete attachment
        await attService.DeleteAsync(andi, list[0].Id);
        var afterDelete = await attService.ListAsync(andi, PR, pr.Name);
        Assert.Equal(1, afterDelete.Count, "1 attachment remains after delete");
    }

    [Test]
    public async Task Attachment_max_size_enforcement()
    {
        var andi = U("andi");
        var pr = await Docs.InsertAsync(andi, PR, new Dictionary<string, object?>
        {
            ["justification"] = "Server hardware upgrade",
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["item_name"] = "Server", ["qty"] = 1m, ["rate"] = 50_000_000m }
            }
        });

        var attService = sp.GetRequiredService<AttachmentService>();

        // Create a dummy stream larger than MaxAttachmentBytes (10 MB + 1 byte)
        var largeStream = new LargeDummyStream(AttachmentService.MaxAttachmentBytes + 1);

        await Assert.Throws<FileRejectedException>(async () =>
        {
            await attService.UploadAsync(andi, PR, pr.Name, "oversized.pdf", largeStream);
        }, "oversized attachment rejection");
    }

    private sealed class LargeDummyStream(long length) : Stream
    {
        private long _pos;
        private static readonly byte[] Header = "%PDF-"u8.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= length) return 0;
            var toRead = (int)Math.Min(count, length - _pos);
            for (int i = 0; i < toRead; i++)
            {
                var idx = _pos + i;
                buffer[offset + i] = idx < Header.Length ? Header[idx] : (byte)'A';
            }
            _pos += toRead;
            return toRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Test]
    public Task Email_template_rendering_variables()
    {
        var template = "Hello {{name}}, your request {{doc.name}} for {{amount}} has been {{status}}!";
        var vars = new Dictionary<string, string?>
        {
            ["name"] = "Budi Hartono",
            ["doc.name"] = "PR-2026-0001",
            ["amount"] = "Rp 15.000.000",
            ["status"] = "Approved"
        };

        var rendered = EmailTemplateEngine.Render(template, vars);
        Assert.Equal("Hello Budi Hartono, your request PR-2026-0001 for Rp 15.000.000 has been Approved!", rendered, "template rendering");

        // Object model rendering
        var model = new { Name = "Sari", Document = "PO-001" };
        var renderedObj = EmailTemplateEngine.Render("Dear {{Name}}, order {{Document}} created.", model);
        Assert.Equal("Dear Sari, order PO-001 created.", renderedObj, "model rendering");

        // Missing placeholder replaced with empty string
        var missing = EmailTemplateEngine.Render("Hello {{missing_key}}!", vars);
        Assert.Equal("Hello !", missing, "missing key replacement");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Email_outgoing_outbox_flow()
    {
        var memSender = new InMemoryEmailSender();
        var outboxStore = new InMemoryDocumentStore();
        var queue = new OutboxEmailQueue(outboxStore, TimeProvider.System);
        var handler = new OutboxEmailHandler(memSender);
        var processor = new OutboxProcessor(outboxStore, [handler], TimeProvider.System);

        var emailMsg = new EmailMessage
        {
            To = ["approver@company.local"],
            Subject = "Purchase Request #PR-100 Requires Approval",
            BodyHtml = "<p>Please review and approve PR-100.</p>",
            BodyText = "Please review and approve PR-100."
        };

        // 1. Queue into Outbox
        await queue.QueueAsync("acme", emailMsg, idempotencyKey: "test-email-1");

        // 2. Process Outbox
        var result = await processor.ProcessDueAsync();
        Assert.Equal(1, result.Processed, "1 email processed in outbox");
        Assert.Equal(0, result.Failed, "0 failed");

        // 3. Verify sent
        Assert.Equal(1, memSender.SentMessages.Count, "1 email sent via sender");
        var sent = memSender.SentMessages[0];
        Assert.Equal("approver@company.local", sent.To[0], "recipient");
        Assert.Equal("Purchase Request #PR-100 Requires Approval", sent.Subject, "subject");
        Assert.Equal("<p>Please review and approve PR-100.</p>", sent.BodyHtml, "html body");
    }

    [Test]
    public async Task Email_outgoing_outbox_retry_and_dead_letter()
    {
        var throwingSender = new ThrowingEmailSender();
        var outboxStore = new InMemoryDocumentStore();
        var queue = new OutboxEmailQueue(outboxStore, TimeProvider.System);
        var handler = new OutboxEmailHandler(throwingSender);
        var processor = new OutboxProcessor(outboxStore, [handler], TimeProvider.System);

        var emailMsg = new EmailMessage
        {
            To = ["fail@company.local"],
            Subject = "Failure test",
            BodyHtml = "<p>Fail</p>"
        };

        await queue.QueueAsync("acme", emailMsg, idempotencyKey: "test-email-fail");

        // Attempt 1
        var r1 = await processor.ProcessDueAsync();
        Assert.Equal(1, r1.Failed, "attempt 1 failed");

        // Simulate 4 more attempts to reach MaxAttempts (5)
        for (int i = 2; i <= 5; i++)
        {
            // Reset next attempt to now so processor picks it up
            var snapshot = outboxStore.Snapshot().First(m => m.IdempotencyKey == "test-email-fail");
            snapshot.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await outboxStore.UpdateAsync(snapshot);

            var r = await processor.ProcessDueAsync();
            if (i == 5)
            {
                Assert.Equal(1, r.DeadLettered, "attempt 5 moved to dead letter");
            }
        }

        var deadMsg = outboxStore.Snapshot().First(m => m.IdempotencyKey == "test-email-fail");
        Assert.True(deadMsg.DeadLettered, "dead letter flag set");
        Assert.Equal(5, deadMsg.Attempts, "attempts count is 5");
    }

    private sealed class ThrowingEmailSender : IEmailSender
    {
        public Task SendAsync(EmailMessage message, CancellationToken ct = default) =>
            throw new InvalidOperationException("SMTP connection refused: Simulated network failure.");
    }

    [Test]
    public async Task Password_reset_token_generation_and_single_use()
    {
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var repo = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();

        // Create a test user with an email
        var user = new UserAccount
        {
            UserName = "reset_tester_" + Guid.NewGuid().ToString("N")[..6],
            FullName = "Reset Test User",
            Email = "tester@docframe.local",
            NormalizedEmail = "TESTER@DOCFRAME.LOCAL",
            TenantId = "acme",
            Roles = ["Employee"]
        };
        var createResult = await users.CreateAsync(user, "OldPassword#123");
        Assert.True(createResult.Succeeded, "user created");

        // 1. Generate password reset token
        var token = await users.GeneratePasswordResetTokenAsync(user);
        Assert.True(!string.IsNullOrEmpty(token), "token generated");

        // 2. Reset password using the token
        var resetResult = await users.ResetPasswordAsync(user, token, "NewPassword#2026");
        Assert.True(resetResult.Succeeded, "password reset succeeded");

        // 3. Verify user can authenticate with new password
        var passwordValid = await users.CheckPasswordAsync(user, "NewPassword#2026");
        Assert.True(passwordValid, "new password is valid");

        var oldPasswordValid = await users.CheckPasswordAsync(user, "OldPassword#123");
        Assert.True(!oldPasswordValid, "old password no longer works");

        // 4. Verify Single-Use: Attempting to reuse the same token must fail!
        var reuseResult = await users.ResetPasswordAsync(user, token, "AnotherPassword#999");
        Assert.True(!reuseResult.Succeeded, "reusing token is rejected (single-use enforced)");
    }

    [Test, PgTest]
    public async Task Attachment_postgres_store_roundtrip()
    {
        var attStore = sp.GetRequiredService<IAttachmentStore>();
        var tenantA = "tenant_att_a";
        var tenantB = "tenant_att_b";
        var docName = "PR-TEST-001";

        var rec1 = new AttachmentRecord
        {
            Id = "att_pg_1",
            TenantId = tenantA,
            DocType = "PurchaseRequest",
            DocName = docName,
            FileName = "quotation.pdf",
            ContentType = "application/pdf",
            Size = 1024,
            Sha256 = "dummy-sha256-hash-1",
            UploadedBy = "user1",
            CreatedAt = DateTimeOffset.UtcNow,
            IsPrivate = true
        };
        var content1 = "%PDF-1.4 dummy postgres test content"u8.ToArray();

        var rec2 = new AttachmentRecord
        {
            Id = "att_pg_2",
            TenantId = tenantA,
            DocType = "PurchaseRequest",
            DocName = docName,
            FileName = "notes.txt",
            ContentType = "text/plain",
            Size = 12,
            Sha256 = "dummy-sha256-hash-2",
            UploadedBy = "user1",
            CreatedAt = DateTimeOffset.UtcNow,
            IsPrivate = true
        };
        var content2 = "hello world!"u8.ToArray();

        // 1. Save attachments in PostgreSQL
        await attStore.SaveAsync(rec1, content1);
        await attStore.SaveAsync(rec2, content2);

        // 2. List attachments for tenantA
        var listA = await attStore.ListAsync(tenantA, "PurchaseRequest", docName);
        Assert.Equal(2, listA.Count, "2 attachments saved in postgres");

        // 3. Verify tenant isolation (tenantB cannot see tenantA's attachments)
        var listB = await attStore.ListAsync(tenantB, "PurchaseRequest", docName);
        Assert.Equal(0, listB.Count, "tenant isolation enforced in postgres attachment list");

        var contentFromOtherTenant = await attStore.GetContentAsync(tenantB, rec1.Id);
        Assert.True(contentFromOtherTenant is null, "tenant isolation enforced in postgres attachment get");

        // 4. Retrieve content
        var content = await attStore.GetContentAsync(tenantA, rec1.Id);
        Assert.True(content is not null, "attachment found");
        Assert.Equal("quotation.pdf", content!.Value.Record.FileName, "filename matched");
        Assert.Equal(content1.Length, content!.Value.Content.Length, "content length matched");

        // 5. Delete attachment
        await attStore.DeleteAsync(tenantA, rec1.Id);
        var remaining = await attStore.ListAsync(tenantA, "PurchaseRequest", docName);
        Assert.Equal(1, remaining.Count, "1 attachment remaining after delete in postgres");
        Assert.Equal("att_pg_2", remaining[0].Id, "remaining id matched");

        // Cleanup
        await attStore.DeleteAsync(tenantA, rec2.Id);
    }
}
