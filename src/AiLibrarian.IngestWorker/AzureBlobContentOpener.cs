using Azure.Identity;
using Azure.Storage.Blobs;

using Microsoft.Extensions.Options;

namespace AiLibrarian.IngestWorker;

/// <summary>Uses a storage connection string (e.g. Azurite) or token credential (<see cref="DefaultAzureCredential"/>) against the blob URI.</summary>
public sealed class AzureBlobContentOpener : IBlobContentOpener
{
	private readonly IOptions<IngestWorkerOptions> _options;

	public AzureBlobContentOpener(IOptions<IngestWorkerOptions> options)
	{
		_options = options;
	}

	/// <inheritdoc />
	public async Task<Stream> OpenReadAsync(Uri blobUri, CancellationToken cancellationToken)
	{
		var blobOpts = _options.Value.Blob;
		BlobClient client;
		if (!string.IsNullOrWhiteSpace(blobOpts.ConnectionString))
		{
			var service = new BlobServiceClient(blobOpts.ConnectionString);
			var builder = new BlobUriBuilder(blobUri);
			client = service.GetBlobContainerClient(builder.BlobContainerName).GetBlobClient(builder.BlobName);
		}
		else if (blobOpts.UseManagedIdentity)
		{
			client = new BlobClient(blobUri, new DefaultAzureCredential());
		}
		else
		{
			throw new InvalidOperationException(
				"IngestWorker:Blob must set ConnectionString (Azurite/dev) or UseManagedIdentity=true for Azure blobs.");
		}

		var response = await client.DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
		return response.Value.Content;
	}
}
