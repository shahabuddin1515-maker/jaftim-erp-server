namespace Jaftim.Application.Abstractions;

/// <summary>Azure Blob wrapper (legacy BlobService/FileService). Names are blob names, not full URLs.</summary>
public interface IBlobStorage
{
    Task<string> UploadAsync(Stream content, string fileName, string? contentType, CancellationToken ct = default);
    Task<Uri> GetReadSasUriAsync(string blobName, TimeSpan validFor, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string blobName, CancellationToken ct = default);
    Task DeleteAsync(string blobName, CancellationToken ct = default);
}
