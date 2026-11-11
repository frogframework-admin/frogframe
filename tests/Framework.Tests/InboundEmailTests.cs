using Framework.Communication;
using Framework.Documents;
using Framework.Email;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Unit & integration tests for Phase 3 Workstream D: Inbound Email & Communication Timeline.</summary>
sealed partial class Tests
{
    private sealed class MockMailboxReader : IMailboxReader
    {
        public long UidValidity { get; set; } = 1000;
        public List<InboundEmailMessage> Messages { get; set; } = [];

        public Task<MailboxFolderInfo> OpenFolderAsync(EmailAccount account, string password, CancellationToken ct = default)
        {
            return Task.FromResult(new MailboxFolderInfo(UidValidity, Messages.Count));
        }

        public Task<IReadOnlyList<InboundEmailMessage>> FetchNewMessagesAsync(
            EmailAccount account,
            string password,
            long lastUid,
            int batchSize = 100,
            CancellationToken ct = default)
        {
            var target = Messages
                .Where(m => m.Uid > lastUid)
                .OrderBy(m => m.Uid)
                .Take(batchSize)
                .ToList();
            return Task.FromResult<IReadOnlyList<InboundEmailMessage>>(target);
        }
    }

    [Test]
    public async Task InboundEmail_deduplication_on_message_id()
    {
        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Support",
            EmailAddress = "support@acme.com",
            ImapHost = "imap.example.com",
            Username = "support",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "andi",
            UidValidity = 1000,
            LastUid = 0
        };
        await accountStore.SaveAsync(account);

        // Add 2 messages with same MessageId
        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 1,
            MessageId = "<dup-001@example.com>",
            Subject = "Test Message 1",
            From = "client@example.com",
            Date = DateTimeOffset.UtcNow
        });
        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 2,
            MessageId = "<dup-001@example.com>", // Same message ID!
            Subject = "Test Message 1 (Copy)",
            From = "client@example.com",
            Date = DateTimeOffset.UtcNow
        });

        var processed = await processor.ProcessAccountAsync(account);
        Assert.Equal(1, processed, "Only one unique message was processed");

        var allUnlinked = await commStore.ListUnlinkedAsync("acme");
        Assert.Equal(1, allUnlinked.Count, "Exactly 1 communication entry stored in database");
        Assert.Equal("<dup-001@example.com>", allUnlinked[0].MessageId, "Message ID matches");
    }

    [Test]
    public async Task InboundEmail_thread_matching_via_in_reply_to()
    {
        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Billing",
            EmailAddress = "billing@acme.com",
            ImapHost = "imap.example.com",
            Username = "billing",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "andi"
        };
        await accountStore.SaveAsync(account);

        // Seed an initial outbound communication linked to document PR-0001
        await commStore.SaveAsync(new CommunicationEntry
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            ReferenceDocType = PR,
            ReferenceName = "PR-0001",
            Direction = CommunicationDirection.Out,
            MessageId = "<out-001@acme.com>",
            Subject = "Purchase Request Inquiry",
            Sender = "andi@acme.com",
            OccurredAt = DateTimeOffset.UtcNow.AddHours(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        });

        // Inbound reply arrived
        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 1,
            MessageId = "<reply-001@client.com>",
            InReplyTo = "<out-001@acme.com>",
            Subject = "Re: Purchase Request Inquiry",
            From = "client@client.com",
            BodyText = "We approve the quotation.",
            Date = DateTimeOffset.UtcNow
        });

        var processed = await processor.ProcessAccountAsync(account);
        Assert.Equal(1, processed, "Processed 1 reply message");

        var docComms = await commStore.ListForDocumentAsync("acme", PR, "PR-0001");
        Assert.Equal(2, docComms.Count, "Document timeline has 2 communications (outbound + reply)");
        Assert.True(docComms.Any(c => c.Direction == CommunicationDirection.In && c.MessageId == "<reply-001@client.com>"),
            "Inbound reply is attached to referenced document");
    }

    [Test]
    public async Task InboundEmail_thread_matching_via_subject_tag()
    {
        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var user = U("andi");
        var doc = await Docs.InsertAsync(user, PR, P(("items", Items(1, 1000)), ("vendor", "VND-0001"), ("request_date", ReqDate()), ("required_by", ReqDate(1))));

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Support",
            EmailAddress = "support@acme.com",
            ImapHost = "imap.example.com",
            Username = "support",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "andi"
        };
        await accountStore.SaveAsync(account);

        // Inbound email with subject tag [#DOC-NAME]
        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 1,
            MessageId = "<tag-001@external.com>",
            Subject = $"Important update regarding [#{doc.Name}] order",
            From = "vendor@external.com",
            BodyText = "Delivery will be on Friday.",
            Date = DateTimeOffset.UtcNow
        });

        var processed = await processor.ProcessAccountAsync(account);
        Assert.Equal(1, processed, "Processed 1 tagged message");

        var docComms = await commStore.ListForDocumentAsync("acme", PR, doc.Name);
        Assert.Equal(1, docComms.Count, "Message matched via subject tag [#NAME]");
        Assert.Equal(doc.Name, docComms[0].ReferenceName, "Communication ReferenceName matches document name");
    }

    [Test]
    public async Task InboundEmail_append_to_doctype_creates_document_with_field_mapping()
    {
        var accounts = sp.GetRequiredService<IUserAccountRepository>();
        await accounts.CreateAsync(new UserAccount
        {
            Id = "service_finance",
            UserName = "service_finance",
            NormalizedUserName = "SERVICE_FINANCE",
            TenantId = "acme",
            FullName = "Service Finance",
            Roles = ["Finance"],
            IsActive = true
        });

        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Customer Intake",
            EmailAddress = "customers@acme.com",
            ImapHost = "imap.example.com",
            Username = "customers",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "service_finance",
            AppendToDocType = "Customer",
            FieldMap = new Dictionary<string, string>
            {
                ["code"] = "subject",
                ["customer_name"] = "sender"
            }
        };
        await accountStore.SaveAsync(account);

        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 1,
            MessageId = "<customer-001@client.com>",
            Subject = "CUST-9901",
            From = "PT Global Mandiri",
            BodyText = "New customer onboarding request.",
            Date = DateTimeOffset.UtcNow
        });

        var processed = await processor.ProcessAccountAsync(account);
        Assert.Equal(1, processed, "Processed 1 intake email");

        var unlinked = await commStore.ListUnlinkedAsync("acme");
        Assert.Equal(0, unlinked.Count, "Email was not left unlinked");

        var comm = (await commStore.GetByMessageIdAsync("acme", "<customer-001@client.com>"))!;
        Assert.Equal("Customer", comm.ReferenceDocType, "Appended to Customer");
        Assert.Equal("CUST-9901", comm.ReferenceName, "Referenced created customer");

        // Verify document was inserted with mapped fields
        var runAsUser = await automationIdentity.ForUserAsync("acme", "service_finance");
        var createdDoc = await Docs.GetAsync(runAsUser, "Customer", "CUST-9901");
        Assert.Equal("PT Global Mandiri", createdDoc.Data["customer_name"]?.ToString(), "Customer name mapped from sender");
    }

    [Test]
    public async Task InboundEmail_append_to_doctype_fails_when_service_user_lacks_create_permission()
    {
        var accounts = sp.GetRequiredService<IUserAccountRepository>();
        await accounts.CreateAsync(new UserAccount
        {
            Id = "service_viewer",
            UserName = "service_viewer",
            NormalizedUserName = "SERVICE_VIEWER",
            TenantId = "acme",
            FullName = "Service Viewer",
            Roles = ["Employee"], // Employee has no create permission on Customer
            IsActive = true
        });

        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        // User "service_viewer" only has Employee role (read-only for Customer, no create permission)
        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Unprivileged Intake",
            EmailAddress = "inbox@acme.com",
            ImapHost = "imap.example.com",
            Username = "inbox",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "service_viewer",
            AppendToDocType = "Customer",
            FieldMap = new Dictionary<string, string>
            {
                ["code"] = "subject",
                ["customer_name"] = "sender"
            }
        };
        await accountStore.SaveAsync(account);

        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 1,
            MessageId = "<unauth-001@hacker.com>",
            Subject = "CUST-HACK",
            From = "Attacker Org",
            Date = DateTimeOffset.UtcNow
        });

        bool caught = false;
        try
        {
            await processor.ProcessAccountAsync(account);
        }
        catch (PermissionDeniedException)
        {
            caught = true;
        }

        Assert.True(caught, "DocumentService.InsertAsync blocked unauthorized service user");
        Assert.Equal(0, (await commStore.ListUnlinkedAsync("acme")).Count, "No communication created on permission failure");
    }

    [Test]
    public async Task InboundEmail_skips_auto_submitted_and_bulk_emails()
    {
        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Autoresponder Test",
            EmailAddress = "support@acme.com",
            ImapHost = "imap.example.com",
            Username = "support",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "andi"
        };
        await accountStore.SaveAsync(account);

        // Auto-responder message
        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 1,
            MessageId = "<auto-001@vacation.com>",
            Subject = "Out of Office: Thank you for your email",
            From = "colleague@vacation.com",
            IsAutoSubmitted = true,
            Date = DateTimeOffset.UtcNow
        });

        var processed = await processor.ProcessAccountAsync(account);
        Assert.Equal(0, processed, "Auto-submitted email was ignored");
        Assert.Equal(0, (await commStore.ListUnlinkedAsync("acme")).Count, "No communications recorded");
    }

    [Test]
    public async Task InboundEmail_html_sanitization_strips_scripts_and_event_handlers()
    {
        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Sanitization Test",
            EmailAddress = "test@acme.com",
            ImapHost = "imap.example.com",
            Username = "test",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "andi"
        };
        await accountStore.SaveAsync(account);

        var maliciousHtml = "<p>Legitimate text</p><script>alert('XSS')</script><img src='https://example.com/logo.png' onerror='alert(document.cookie)' />";
        reader.Messages.Add(new InboundEmailMessage
        {
            Uid = 1,
            MessageId = "<xss-001@evil.com>",
            Subject = "Special Offer",
            From = "promo@evil.com",
            BodyHtml = maliciousHtml,
            Date = DateTimeOffset.UtcNow
        });

        await processor.ProcessAccountAsync(account);

        var saved = await commStore.GetByMessageIdAsync("acme", "<xss-001@evil.com>");
        Assert.True(saved is not null, "Communication saved");
        Assert.True(!saved!.ContentHtml!.Contains("<script>", StringComparison.OrdinalIgnoreCase), "Script tags stripped");
        Assert.True(!saved.ContentHtml.Contains("onerror", StringComparison.OrdinalIgnoreCase), "Event handler onerror stripped");
        Assert.True(saved.ContentHtml.Contains("Legitimate text"), "Legitimate text preserved");
    }

    [Test]
    public async Task InboundEmail_resumes_cleanly_after_interrupted_run()
    {
        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader();
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Resumption Test",
            EmailAddress = "test@acme.com",
            ImapHost = "imap.example.com",
            Username = "test",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "andi",
            LastUid = 0
        };
        await accountStore.SaveAsync(account);

        // 3 messages
        reader.Messages.Add(new InboundEmailMessage { Uid = 10, MessageId = "<msg-10@test.com>", Subject = "M10", From = "a@test.com", Date = DateTimeOffset.UtcNow });
        reader.Messages.Add(new InboundEmailMessage { Uid = 20, MessageId = "<msg-20@test.com>", Subject = "M20", From = "a@test.com", Date = DateTimeOffset.UtcNow });
        reader.Messages.Add(new InboundEmailMessage { Uid = 30, MessageId = "<msg-30@test.com>", Subject = "M30", From = "a@test.com", Date = DateTimeOffset.UtcNow });

        // Run batch
        await processor.ProcessAccountAsync(account);
        var updatedAcc = await accountStore.GetByIdAsync("acme", account.Id);
        Assert.Equal(30, updatedAcc!.LastUid, "LastUid advanced to 30");

        // Add 1 more message
        reader.Messages.Add(new InboundEmailMessage { Uid = 40, MessageId = "<msg-40@test.com>", Subject = "M40", From = "a@test.com", Date = DateTimeOffset.UtcNow });

        var secondRun = await processor.ProcessAccountAsync(updatedAcc);
        Assert.Equal(1, secondRun, "Only newly fetched message with UID > 30 processed");
        Assert.Equal(4, (await commStore.ListUnlinkedAsync("acme")).Count, "Total 4 communications without duplication");
    }

    [Test]
    public async Task InboundEmail_handles_uid_validity_change_without_duplicates()
    {
        var accountStore = new InMemoryEmailAccountStore();
        var commStore = new InMemoryCommunicationStore();
        var reader = new MockMailboxReader { UidValidity = 1000 };
        var secretProtector = sp.GetRequiredService<ISecretProtector>();
        var automationIdentity = sp.GetRequiredService<IAutomationIdentity>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        var time = sp.GetRequiredService<TimeProvider>();

        var processor = new InboundEmailProcessor(
            accountStore,
            commStore,
            reader,
            secretProtector,
            automationIdentity,
            Docs,
            meta,
            time);

        var account = new EmailAccount
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            Name = "Validity Test",
            EmailAddress = "test@acme.com",
            ImapHost = "imap.example.com",
            Username = "test",
            PasswordEnc = secretProtector.Protect("secret"),
            ServiceUserId = "andi",
            UidValidity = 1000,
            LastUid = 50
        };
        await accountStore.SaveAsync(account);

        // Initial message already processed earlier
        await commStore.SaveAsync(new CommunicationEntry
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            MessageId = "<existing@test.com>",
            Subject = "Old Message",
            Sender = "a@test.com",
            Direction = CommunicationDirection.In,
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Server UIDVALIDITY changed to 2000! UIDs reset on server.
        reader.UidValidity = 2000;
        reader.Messages.Add(new InboundEmailMessage { Uid = 1, MessageId = "<existing@test.com>", Subject = "Old Message Re-read", From = "a@test.com", Date = DateTimeOffset.UtcNow });
        reader.Messages.Add(new InboundEmailMessage { Uid = 2, MessageId = "<new@test.com>", Subject = "New Message", From = "b@test.com", Date = DateTimeOffset.UtcNow });

        var processed = await processor.ProcessAccountAsync(account);
        Assert.Equal(1, processed, "Only the truly new message processed, duplicate skipped by Message-ID");

        var accAfter = await accountStore.GetByIdAsync("acme", account.Id);
        Assert.Equal(2000, accAfter!.UidValidity, "Account UidValidity updated to new server value");
        Assert.Equal(2, accAfter.LastUid, "LastUid advanced under new validity");
    }

    [Test]
    public async Task InboundEmail_communication_service_enforces_read_access()
    {
        var commStore = new InMemoryCommunicationStore();
        var commService = new CommunicationService(commStore, Docs);

        var userAndi = U("andi");
        var doc = await Docs.InsertAsync(userAndi, PR, P(("items", Items(1, 1000)), ("vendor", "VND-0001"), ("request_date", ReqDate()), ("required_by", ReqDate(1))));

        await commStore.SaveAsync(new CommunicationEntry
        {
            Id = Guid.NewGuid(),
            TenantId = "acme",
            ReferenceDocType = PR,
            ReferenceName = doc.Name,
            Direction = CommunicationDirection.In,
            MessageId = "<pr-doc-comm@test.com>",
            Subject = "Quotation received",
            Sender = "vendor@test.com",
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // 1. Andi has read permission on this document -> succeeds
        var andiComms = await commService.ListForDocumentAsync(userAndi, PR, doc.Name);
        Assert.Equal(1, andiComms.Count, "User with read access can list document communications");

        // 2. User within same tenant without read permission (Guest role has no read access to PR)
        var unprivilegedUser = new UserContext(
            Id: "stranger",
            FullName: "Stranger",
            TenantId: "acme",
            Roles: new HashSet<string> { "Guest" },
            Attributes: new Dictionary<string, string>());

        bool denied = false;
        try
        {
            await commService.ListForDocumentAsync(unprivilegedUser, PR, doc.Name);
        }
        catch (PermissionDeniedException)
        {
            denied = true;
        }
        Assert.True(denied, "User without document read permission cannot view its communications");

        // 3. User from another tenant -> DocumentNotFoundException
        var otherTenantUser = new UserContext(
            Id: "stranger",
            FullName: "Stranger",
            TenantId: "other_corp",
            Roles: new HashSet<string> { "Employee" },
            Attributes: new Dictionary<string, string>());

        bool notFound = false;
        try
        {
            await commService.ListForDocumentAsync(otherTenantUser, PR, doc.Name);
        }
        catch (DocumentNotFoundException)
        {
            notFound = true;
        }
        Assert.True(notFound, "Cross-tenant access to document communications is strictly blocked");
    }
}
