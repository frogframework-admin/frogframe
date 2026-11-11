using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Tests for Phase 3 Step 0: Shared Foundations (EventTypes, RetryPolicy, AutomationIdentity, SecretProtector).</summary>
sealed partial class Tests
{
    [Test]
    public Task EventTypes_constants_and_action_casing()
    {
        Assert.Equal("document.inserted", EventTypes.DocumentInserted, "DocumentInserted constant");
        Assert.Equal("document.updated", EventTypes.DocumentUpdated, "DocumentUpdated constant");
        Assert.Equal("document.deleted", EventTypes.DocumentDeleted, "DocumentDeleted constant");
        Assert.Equal("document.amended", EventTypes.DocumentAmended, "DocumentAmended constant");
        Assert.Equal("workflow.transitioned", EventTypes.WorkflowTransitioned, "WorkflowTransitioned constant");
        Assert.Equal("document.submit", EventTypes.DocumentSubmit, "DocumentSubmit constant");
        Assert.Equal("document.cancel", EventTypes.DocumentCancel, "DocumentCancel constant");

        Assert.Equal("document.submit", EventTypes.DocumentAction("Submit"), "DocumentAction normalizes to lowercase");
        Assert.Equal("document.cancel", EventTypes.DocumentAction("CANCEL "), "DocumentAction trims and lowercases");
        Assert.Equal("document.ajukan", EventTypes.DocumentAction("Ajukan"), "DocumentAction handles custom actions");
        return Task.CompletedTask;
    }

    [Test]
    public async Task EventTypes_document_lifecycle_emits_expected_events()
    {
        var outboxStore = sp.GetRequiredService<IOutboxStore>();
        var user = U("andi");

        // 1. Insert document with vendor so workflow action can succeed
        var doc = await Docs.InsertAsync(user, PR, P(("items", Items(1, 1000)), ("vendor", "VND-0001"), ("request_date", ReqDate()), ("required_by", ReqDate(1))));
        var insertedEvt = outboxStore.Snapshot().LastOrDefault(m => m.Type == EventTypes.DocumentInserted);
        Assert.True(insertedEvt is not null, "DocumentInserted event was emitted to outbox");
        Assert.Equal(doc.Name, insertedEvt!.Payload["name"], "Event payload contains document name");

        // 2. Update document
        doc = await Docs.UpdateAsync(user, PR, doc.Name, doc.Version, P(("justification", "Updated reason")));
        var updatedEvt = outboxStore.Snapshot().LastOrDefault(m => m.Type == EventTypes.DocumentUpdated);
        Assert.True(updatedEvt is not null, "DocumentUpdated event was emitted to outbox");

        // 3. Workflow action (PurchaseRequest has workflow) -> workflow.transitioned
        await Docs.RunActionAsync(user, PR, doc.Name, "Ajukan", doc.Version, "Test submit action");
        var actionEvt = outboxStore.Snapshot().LastOrDefault(m => m.Type == EventTypes.WorkflowTransitioned);
        Assert.True(actionEvt is not null, "Workflow transition emits workflow.transitioned");
        Assert.Equal("Ajukan", actionEvt!.Payload["action"], "Payload preserves action name");
    }

    [Test]
    public Task RetryPolicy_exponential_backoff_and_limit()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), RetryPolicy.GetBackoff(1), "Attempt 1 has 2s backoff");
        Assert.Equal(TimeSpan.FromSeconds(4), RetryPolicy.GetBackoff(2), "Attempt 2 has 4s backoff");
        Assert.Equal(TimeSpan.FromSeconds(8), RetryPolicy.GetBackoff(3), "Attempt 3 has 8s backoff");
        Assert.Equal(TimeSpan.FromSeconds(16), RetryPolicy.GetBackoff(4), "Attempt 4 has 16s backoff");
        Assert.Equal(TimeSpan.FromSeconds(32), RetryPolicy.GetBackoff(5), "Attempt 5 has 32s backoff");

        Assert.True(RetryPolicy.ShouldRetry(0), "Attempt 0 should retry");
        Assert.True(RetryPolicy.ShouldRetry(1), "Attempt 1 should retry");
        Assert.True(RetryPolicy.ShouldRetry(4), "Attempt 4 should retry");
        Assert.True(!RetryPolicy.ShouldRetry(5), "Attempt 5 should NOT retry (dead-lettered)");
        Assert.True(!RetryPolicy.ShouldRetry(6), "Attempt 6 should NOT retry");

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.AddSeconds(2), RetryPolicy.CalculateNextAttempt(1, now), "CalculateNextAttempt adds backoff to base time");
        return Task.CompletedTask;
    }

    [Test]
    public async Task AutomationIdentity_resolves_active_user_context()
    {
        var accounts = sp.GetRequiredService<IUserAccountRepository>();
        var identity = new AutomationIdentity(accounts);

        // 1. Valid active user in acme tenant
        var userCtx = await identity.ForUserAsync("acme", "andi");
        Assert.Equal("andi", userCtx.Id, "Loaded correct user ID");
        Assert.Equal("acme", userCtx.TenantId, "Loaded correct tenant ID");
        Assert.True(userCtx.Roles.Count > 0, "Loaded user roles");

        // 2. Inactive user rejection
        var inactiveUser = new UserAccount
        {
            Id = "disabled_user",
            UserName = "disabled_user",
            NormalizedUserName = "DISABLED_USER",
            TenantId = "acme",
            IsActive = false
        };
        await accounts.CreateAsync(inactiveUser);
        var inactiveEx = false;
        try { await identity.ForUserAsync("acme", "disabled_user"); } catch (InvalidOperationException) { inactiveEx = true; }
        Assert.True(inactiveEx, "Inactive user account is rejected");

        // 3. Mismatched tenant rejection
        var mismatchEx = false;
        try { await identity.ForUserAsync("other_tenant", "andi"); } catch (InvalidOperationException) { mismatchEx = true; }
        Assert.True(mismatchEx, "Account from another tenant is rejected");

        // 4. Non-existent user rejection
        var notFoundEx = false;
        try { await identity.ForUserAsync("acme", "non_existent_user"); } catch (InvalidOperationException) { notFoundEx = true; }
        Assert.True(notFoundEx, "Non-existent user account is rejected");
    }

    [Test]
    public async Task AutomationIdentity_permission_enforcement_on_document_operations()
    {
        var accounts = sp.GetRequiredService<IUserAccountRepository>();
        var identity = new AutomationIdentity(accounts);

        // Create a restricted user account with no PR permissions
        var restrictedAccount = new UserAccount
        {
            Id = "viewer_only",
            UserName = "viewer_only",
            NormalizedUserName = "VIEWER_ONLY",
            TenantId = "acme",
            Roles = ["Staff"], // Has no Create permission on PurchaseRequest
            IsActive = true
        };
        await accounts.CreateAsync(restrictedAccount);

        var runAsUser = await identity.ForUserAsync("acme", "viewer_only");
        var denied = false;
        try
        {
            await Docs.InsertAsync(runAsUser, PR, P(("items", Items(1, 1000)), ("request_date", ReqDate()), ("required_by", ReqDate(1))));
        }
        catch (PermissionDeniedException)
        {
            denied = true;
        }
        Assert.True(denied, "Automation task running under user without create permission is denied");
    }

    [Test]
    public Task SecretProtector_roundtrip_encryption()
    {
        ISecretProtector protector = new InMemorySecretProtector();
        var secret = "whsec_supersecretkey123456789";

        var encrypted = protector.Protect(secret);
        Assert.True(encrypted != secret, "Encrypted text is different from plaintext");
        Assert.True(!string.IsNullOrEmpty(encrypted), "Encrypted text is not empty");

        var decrypted = protector.Unprotect(encrypted);
        Assert.Equal(secret, decrypted, "Decrypted text matches original plaintext");

        Assert.Equal("", protector.Protect(""), "Empty string protection returns empty");
        Assert.Equal("", protector.Unprotect(""), "Empty string unprotect returns empty");
        return Task.CompletedTask;
    }
}

