using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Jaftim.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Jaftim.Infrastructure.Services;

public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";
    public string ConnectionString { get; set; } = string.Empty;
    /// <summary>Legacy setting AzureContainerName ("uicfiles").</summary>
    public string ContainerName { get; set; } = "uicfiles";
}

/// <summary>Replaces the legacy BlobService (Microsoft.WindowsAzure.Storage) with Azure.Storage.Blobs. Same behaviour: GUID-suffixed names, SAS reads.</summary>
public sealed class AzureBlobStorage(IOptions<BlobStorageOptions> options) : IBlobStorage
{
    private readonly BlobContainerClient _container =
        new(options.Value.ConnectionString, options.Value.ContainerName);

    public async Task<string> UploadAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        string blobName = $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}{Path.GetExtension(fileName)}";
        BlobClient blob = _container.GetBlobClient(blobName);
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType ?? "application/octet-stream" },
        }, ct);
        return blobName;
    }

    public Task<Uri> GetReadSasUriAsync(string blobName, TimeSpan validFor, CancellationToken ct = default)
    {
        BlobClient blob = _container.GetBlobClient(blobName);
        if (!blob.CanGenerateSasUri)
            throw new InvalidOperationException("Blob client cannot generate SAS - use a connection string with an account key.");
        return Task.FromResult(blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(validFor)));
    }

    public async Task<Stream> OpenReadAsync(string blobName, CancellationToken ct = default) =>
        await _container.GetBlobClient(blobName).OpenReadAsync(cancellationToken: ct);

    public async Task DeleteAsync(string blobName, CancellationToken ct = default) =>
        await _container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);
}
