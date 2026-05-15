namespace AiLibrarian.Api.BlobStorage;

/// <summary>No-op implementation when <see cref="BlobStorageOptions.ConnectionString"/> is unset.</summary>
public sealed class NullBlobUploadService : IBlobUploadService
{
	public Task<UploadedBlobInfo> UploadAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken) =>
		Task.FromException<UploadedBlobInfo>(new InvalidOperationException("Blob storage is not configured."));
}
