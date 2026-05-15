namespace AiLibrarian.IngestWorker;

/// <summary>Opens a readable stream for blob bodies referenced by ingest jobs.</summary>
public interface IBlobContentOpener
{
	Task<Stream> OpenReadAsync(Uri blobUri, CancellationToken cancellationToken);
}
