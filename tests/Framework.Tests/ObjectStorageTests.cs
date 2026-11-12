using Framework.Files;
using Framework.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Object storage (Storage:Provider = S3): contents in the bucket, records in the database.</summary>
sealed partial class Tests
{
    private static FileRecord TestFile(string id, string tenant = "acme", long size = 3, DateTimeOffset? created = null) => new()
    {
        Id = id, TenantId = tenant, UploadedBy = "andi", ContentType = "image/png", Size = size, Sha256 = "x",
        CreatedAt = created ?? DateTimeOffset.UtcNow,
    };

    [Test]
    public async Task ObjectStorage_file_contents_go_to_the_bucket_and_records_stay_in_the_store()
    {
        var inner = new InMemoryFileStore();
        var blobs = new InMemoryBlobStorage();
        var store = new BlobBackedFileStore(inner, blobs);

        await store.SaveAsync(TestFile("f1"), [1, 2, 3]);
        Assert.True(blobs.Keys.Contains("acme/files/f1"), "content in the bucket under the tenant prefix");
        Assert.Equal(0, (await inner.GetAsync("acme", "f1"))!.Value.Content.Length, "database row holds no content");
        Assert.Equal("1,2,3", string.Join(",", (await store.GetAsync("acme", "f1"))!.Value.Content), "read back from the bucket");
        Assert.True(await store.GetAsync("other", "f1") is null, "tenant isolation unchanged");

        // A row written before the switch keeps its content in the database and stays readable.
        await inner.SaveAsync(TestFile("old"), [9, 9]);
        Assert.Equal("9,9", string.Join(",", (await store.GetAsync("acme", "old"))!.Value.Content), "old rows still served from the database");

        // Missing object → not found (no empty file).
        await inner.SaveAsync(TestFile("lost"), []);
        Assert.True(await store.GetAsync("acme", "lost") is null, "missing object is reported as not found");

        // Orphan cleanup also deletes the objects.
        await store.SaveAsync(TestFile("orphan", created: DateTimeOffset.UtcNow.AddDays(-2)), [7]);
        await store.AttachAsync("acme", "f1", "Employee", "EMP-1", "photo");
        var pruned = await store.PruneOrphansAsync(DateTimeOffset.UtcNow.AddDays(-1));
        Assert.True(pruned >= 1, "orphan pruned");
        Assert.True(!blobs.Keys.Contains("acme/files/orphan"), "orphan object deleted");
        Assert.True(blobs.Keys.Contains("acme/files/f1"), "attached file kept");
    }

    [Test]
    public async Task ObjectStorage_attachments_and_failed_saves()
    {
        var blobs = new InMemoryBlobStorage();
        var store = new BlobBackedAttachmentStore(new InMemoryAttachmentStore(), blobs);
        var record = new AttachmentRecord
        {
            Id = "a1", TenantId = "acme", DocType = "PurchaseRequest", DocName = "PR-1", FileName = "quote.pdf",
            ContentType = "application/pdf", Size = 4, Sha256 = "x", UploadedBy = "andi", CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveAsync(record, [1, 2, 3, 4]);
        Assert.Equal(4, (await store.GetContentAsync("acme", "a1"))!.Value.Content.Length, "content from the bucket");
        Assert.Equal(1, (await store.ListAsync("acme", "PurchaseRequest", "PR-1")).Count, "listing from the database");
        await store.DeleteAsync("acme", "a1");
        Assert.True(blobs.Keys.Count == 0, "object deleted with the attachment");

        // When the database insert fails, the uploaded object is removed again.
        var failing = new BlobBackedFileStore(new FailingFileStore(), blobs);
        await Assert.Throws<InvalidOperationException>(() => failing.SaveAsync(TestFile("f2"), [1]), "error passed on");
        Assert.True(!blobs.Keys.Contains("acme/files/f2"), "no leftover object");
    }

    [Test]
    public Task ObjectStorage_is_wired_only_when_selected()
    {
        IServiceProvider Build(Dictionary<string, string?> settings)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IFileStore, InMemoryFileStore>();
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            services.AddFrogFrameObjectStorage(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            return services.BuildServiceProvider();
        }

        var db = Build(new() { ["Storage:Provider"] = "Database" });
        Assert.True(db.GetRequiredService<IFileStore>() is InMemoryFileStore, "Database: stores unchanged");
        Assert.True(db.GetService<IBlobStorage>() is null, "no object storage");

        var s3 = Build(new()
        {
            ["Storage:Provider"] = "S3", ["Storage:S3:ServiceUrl"] = "http://seaweedfs:8333",
            ["Storage:S3:AccessKey"] = "k", ["Storage:S3:SecretKey"] = "s", ["Storage:S3:Bucket"] = "frogframe",
        });
        Assert.True(s3.GetRequiredService<IFileStore>() is BlobBackedFileStore, "S3: file store wrapped");
        Assert.True(s3.GetRequiredService<IAttachmentStore>() is BlobBackedAttachmentStore, "S3: attachment store wrapped");
        Assert.Equal("s3://frogframe", s3.GetRequiredService<IBlobStorage>().Name, "bucket name");

        Assert.Throws<InvalidOperationException>(() => Build(new() { ["Storage:Provider"] = "S3" }), "missing keys rejected at startup");
        return Task.CompletedTask;
    }

    private sealed class FailingFileStore : IFileStore
    {
        public Task SaveAsync(FileRecord record, byte[] content, CancellationToken ct = default) => throw new InvalidOperationException("db down");
        public Task<FileRecord?> GetInfoAsync(string tenantId, string id, CancellationToken ct = default) => Task.FromResult<FileRecord?>(null);
        public Task<(FileRecord Record, byte[] Content)?> GetAsync(string tenantId, string id, CancellationToken ct = default) => Task.FromResult<(FileRecord, byte[])?>(null);
        public Task AttachAsync(string tenantId, string id, string docType, string docName, string field, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PruneOrphansAsync(DateTimeOffset cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
