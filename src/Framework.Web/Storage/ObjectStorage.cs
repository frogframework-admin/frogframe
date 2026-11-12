using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Framework.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Framework.Storage;

/// <summary>
/// Storage:Provider = "Database" (default: contents in PostgreSQL / memory) or "S3" (any S3-compatible object store:
/// SeaweedFS, MinIO, Ceph RGW, AWS S3, …). Settings under Storage:S3.
/// </summary>
public sealed class S3StorageOptions
{
    /// <summary>Endpoint, e.g. http://seaweedfs:8333. Empty = AWS S3 in <see cref="Region"/>.</summary>
    public string? ServiceUrl { get; set; }
    public string Bucket { get; set; } = "frogframe";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public string Region { get; set; } = "us-east-1";
    /// <summary>bucket in the path (http://host/bucket/key); needed by SeaweedFS, MinIO and Ceph without wildcard DNS.</summary>
    public bool ForcePathStyle { get; set; } = true;
    /// <summary>Create the bucket at startup when it does not exist.</summary>
    public bool CreateBucket { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 30;

    public static bool IsEnabled(IConfiguration configuration) =>
        string.Equals(configuration["Storage:Provider"], "S3", StringComparison.OrdinalIgnoreCase);

    public static S3StorageOptions From(IConfiguration configuration)
    {
        var o = new S3StorageOptions();
        configuration.GetSection("Storage:S3").Bind(o);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Bucket)) missing.Add("Storage:S3:Bucket");
        if (string.IsNullOrWhiteSpace(o.AccessKey)) missing.Add("Storage:S3:AccessKey");
        if (string.IsNullOrWhiteSpace(o.SecretKey)) missing.Add("Storage:S3:SecretKey");
        if (missing.Count > 0)
            throw new InvalidOperationException($"Storage:Provider = S3 needs {string.Join(", ", missing)}.");
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl) && !Uri.TryCreate(o.ServiceUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException($"Storage:S3:ServiceUrl '{o.ServiceUrl}' is not an absolute URL.");
        return o;
    }
}

/// <summary>Object storage over the S3 API (AWS SDK), for any S3-compatible server.</summary>
public sealed class S3BlobStorage : IBlobStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly S3StorageOptions _options;

    public S3BlobStorage(S3StorageOptions options)
    {
        _options = options;
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300)),
            // S3-compatible servers do not all support the newer flexible checksums (aws-chunked trailers).
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }
        _client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public string Name => $"s3://{_options.Bucket}";

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        using var stream = new MemoryStream(content, writable: false);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            AutoCloseStream = false,
            UseChunkEncoding = false,
        }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _options.Bucket, Key = key }, ct);
            using var buffer = new MemoryStream(response.ContentLength > 0 ? (int)Math.Min(response.ContentLength, int.MaxValue) : 0);
            await response.ResponseStream.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _options.Bucket, Key = key }, ct);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
    }

    /// <summary>Creates the bucket when it does not exist. True when the bucket is usable.</summary>
    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        if (await AmazonS3Util.DoesS3BucketExistV2Async(_client, _options.Bucket)) return;
        await _client.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket, UseClientRegion = true }, ct);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Checks (and optionally creates) the bucket at startup, so a wrong endpoint or key shows up in the log at once.</summary>
public sealed class S3StartupCheck(S3BlobStorage storage, S3StorageOptions options, ILogger<S3StartupCheck> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (options.CreateBucket) await storage.EnsureBucketAsync(cancellationToken);
            log.LogInformation("File contents are stored in {Storage} ({Endpoint})", storage.Name, options.ServiceUrl ?? "AWS " + options.Region);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not stop the app: uploads fail with a clear error until the object store is reachable.
            log.LogError(ex, "Object storage {Storage} at {Endpoint} is not reachable; uploads and downloads will fail", storage.Name, options.ServiceUrl);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ObjectStorageSetup
{
    /// <summary>
    /// With Storage:Provider = S3: file and attachment contents go to the bucket; their records stay in the database.
    /// Call AFTER the persistence provider (AddFrogFrameworkPostgreSql), because it wraps the registered stores.
    /// </summary>
    public static IServiceCollection AddFrogFrameObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        if (!S3StorageOptions.IsEnabled(configuration)) return services;

        var options = S3StorageOptions.From(configuration);
        services.AddSingleton(options);
        services.AddSingleton<S3BlobStorage>();
        services.AddSingleton<IBlobStorage>(sp => sp.GetRequiredService<S3BlobStorage>());
        services.AddHostedService<S3StartupCheck>();

        Decorate<IFileStore>(services, (sp, inner) =>
            new BlobBackedFileStore(inner, sp.GetRequiredService<IBlobStorage>(), sp.GetService<ILogger<BlobBackedFileStore>>()));
        Decorate<IAttachmentStore>(services, (sp, inner) =>
            new BlobBackedAttachmentStore(inner, sp.GetRequiredService<IBlobStorage>(), sp.GetService<ILogger<BlobBackedAttachmentStore>>()));
        return services;
    }

    /// <summary>Replaces the last registration of <typeparamref name="T"/> by a singleton that wraps it.</summary>
    internal static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> wrap) where T : class
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not registered; call AddFrogFramework first.");
        services.Remove(descriptor);
        services.AddSingleton<T>(sp =>
        {
            var inner = descriptor.ImplementationInstance as T
                ?? descriptor.ImplementationFactory?.Invoke(sp) as T
                ?? (T)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
            return wrap(sp, inner);
        });
    }
}
