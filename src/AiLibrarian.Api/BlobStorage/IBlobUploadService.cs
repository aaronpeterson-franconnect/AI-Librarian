namespace AiLibrarian.Api.BlobStorage;

public interface IBlobUploadService
{
	Task<UploadedBlobInfo> UploadAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken);
}
