using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using Microsoft.Extensions.Options;

namespace AiLibrarian.Api.BlobStorage;

public sealed class AzureBlobUploadService : IBlobUploadService
{
	private readonly BlobStorageOptions _options;

	public AzureBlobUploadService(IOptions<BlobStorageOptions> options)
	{
		_options = options.Value;
	}

	public async Task<UploadedBlobInfo> UploadAsync(
		Stream content,
		string fileName,
		string? contentType,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(_options.ConnectionString))
		{
			throw new InvalidOperationException("Blob storage connection string is empty.");
		}

		var safeName = SanitizeFileName(fileName);
		var blobName = $"uploads/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}-{safeName}";
		var service = new BlobServiceClient(_options.ConnectionString);
		var container = service.GetBlobContainerClient(_options.ContainerName);
		await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken).ConfigureAwait(false);
		var blob = container.GetBlobClient(blobName);
		var headers = new BlobHttpHeaders
		{
			ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim(),
		};
		await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = headers }, cancellationToken).ConfigureAwait(false);
		return new UploadedBlobInfo(blob.Uri.ToString(), fileName, headers.ContentType);
	}

	private static string SanitizeFileName(string fileName)
	{
		var name = Path.GetFileName(fileName.Trim());
		if (string.IsNullOrEmpty(name))
		{
			return "upload.bin";
		}

		foreach (var c in Path.GetInvalidFileNameChars())
		{
			name = name.Replace(c, '_');
		}

		return name.Length > 200 ? name[..200] : name;
	}
}
