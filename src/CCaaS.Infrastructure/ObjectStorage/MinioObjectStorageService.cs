using Microsoft.Extensions.Configuration;
using Minio;
using Minio.DataModel.Args;

namespace CCaaS.Infrastructure.ObjectStorage;

// Section 6/18 - "Object Storage: MinIO dev / S3-compatible prod - Recordings and attachments
// outside SQL." SQL Server only ever stores the object storage KEY, never the bytes.
public interface IObjectStorageService
{
    Task<string> UploadAsync(string bucket, string objectKey, Stream content, string contentType, CancellationToken ct = default);
    Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken ct = default);
    Task<string> GetPresignedUrlAsync(string bucket, string objectKey, TimeSpan expiry, CancellationToken ct = default);
    Task EnsureBucketExistsAsync(string bucket, CancellationToken ct = default);
}

public class MinioObjectStorageService : IObjectStorageService
{
    private readonly IMinioClient _client;

    public MinioObjectStorageService(IConfiguration configuration)
    {
        _client = new MinioClient()
            .WithEndpoint(configuration["ObjectStorage:Endpoint"] ?? "localhost:9000")
            .WithCredentials(configuration["ObjectStorage:AccessKey"], configuration["ObjectStorage:SecretKey"])
            .WithSSL(bool.Parse(configuration["ObjectStorage:UseSsl"] ?? "false"))
            .Build();
    }

    public async Task EnsureBucketExistsAsync(string bucket, CancellationToken ct = default)
    {
        var exists = await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket), ct);
        if (!exists)
            await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket), ct);
    }

    public async Task<string> UploadAsync(string bucket, string objectKey, Stream content, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketExistsAsync(bucket, ct);
        await _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType(contentType), ct);
        return objectKey;
    }

    public async Task<Stream> DownloadAsync(string bucket, string objectKey, CancellationToken ct = default)
    {
        var memoryStream = new MemoryStream();
        await _client.GetObjectAsync(new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithCallbackStream(stream => stream.CopyTo(memoryStream)), ct);
        memoryStream.Position = 0;
        return memoryStream;
    }

    public async Task<string> GetPresignedUrlAsync(string bucket, string objectKey, TimeSpan expiry, CancellationToken ct = default)
    {
        return await _client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithExpiry((int)expiry.TotalSeconds));
    }
}
